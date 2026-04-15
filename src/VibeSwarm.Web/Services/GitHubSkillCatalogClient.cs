using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Default <see cref="IGitHubSkillCatalogClient"/> targeting <c>anthropics/skills</c> on
/// github.com. Uses the public REST API for discovery (cached) and the tarball endpoint for
/// install-time folder retrieval.
/// </summary>
public sealed class GitHubSkillCatalogClient : IGitHubSkillCatalogClient
{
	internal const string HttpClientName = "github-skills";
	internal const string CatalogCacheKey = "github-skills::catalog";
	internal const string RepoOwner = "anthropics";
	internal const string RepoName = "skills";
	private static readonly TimeSpan CatalogTtl = TimeSpan.FromHours(24);

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		PropertyNameCaseInsensitive = true,
	};

	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IMemoryCache _cache;
	private readonly ISettingsService _settingsService;
	private readonly ILogger<GitHubSkillCatalogClient> _logger;

	public GitHubSkillCatalogClient(
		IHttpClientFactory httpClientFactory,
		IMemoryCache cache,
		ISettingsService settingsService,
		ILogger<GitHubSkillCatalogClient>? logger = null)
	{
		_httpClientFactory = httpClientFactory;
		_cache = cache;
		_settingsService = settingsService;
		_logger = logger ?? NullLogger<GitHubSkillCatalogClient>.Instance;
	}

	public async Task<IReadOnlyList<MarketplaceSkillSummary>> ListSkillsAsync(CancellationToken cancellationToken = default)
	{
		if (_cache.TryGetValue(CatalogCacheKey, out IReadOnlyList<MarketplaceSkillSummary>? cached) && cached is not null)
		{
			return cached;
		}

		var catalog = await FetchCatalogAsync(cancellationToken);
		_cache.Set(CatalogCacheKey, catalog, CatalogTtl);
		return catalog;
	}

	public async Task<Stream> DownloadTarballAsync(string gitRef, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gitRef))
		{
			throw new ArgumentException("Git ref is required.", nameof(gitRef));
		}

		var client = await CreateClientAsync(cancellationToken);
		var url = $"repos/{RepoOwner}/{RepoName}/tarball/{gitRef}";
		var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		response.EnsureSuccessStatusCode();

		// Callers dispose the returned stream. We don't buffer the whole tarball in memory.
		return await response.Content.ReadAsStreamAsync(cancellationToken);
	}

	public void InvalidateCache()
	{
		_cache.Remove(CatalogCacheKey);
	}

	private async Task<IReadOnlyList<MarketplaceSkillSummary>> FetchCatalogAsync(CancellationToken cancellationToken)
	{
		var client = await CreateClientAsync(cancellationToken);

		// Default branch SHA gives us a stable ref to stamp on install records so we can
		// detect upstream drift later. If the SHA call fails we fall back to "HEAD".
		var headRef = await TryGetDefaultBranchShaAsync(client, cancellationToken) ?? "HEAD";

		var contents = await GetAsync<List<GitHubContentItem>>(
			client,
			$"repos/{RepoOwner}/{RepoName}/contents/",
			cancellationToken)
			?? [];

		var folderItems = contents.Where(item => item.Type == "dir").ToList();

		var summaries = new List<MarketplaceSkillSummary>();
		foreach (var folder in folderItems)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var manifest = await TryGetManifestAsync(client, folder.Path, cancellationToken);
				if (manifest is null)
				{
					// Folders without a SKILL.md aren't skills — quietly skip.
					continue;
				}

				var (metadata, _) = SkillManifestParser.ParseFrontMatter(manifest);
				var name = SkillManifestParser.GetValue(metadata, "name") ?? folder.Name;
				var description = SkillManifestParser.GetValue(metadata, "description");
				var allowedTools = SkillManifestParser.GetValue(metadata, "allowed-tools");

				summaries.Add(new MarketplaceSkillSummary
				{
					Slug = folder.Name,
					Name = name,
					Description = description,
					AllowedTools = allowedTools,
					Ref = headRef,
				});
			}
			catch (Exception ex) when (ex is HttpRequestException or JsonException)
			{
				_logger.LogWarning(ex, "Failed to read manifest for marketplace skill {Folder}; omitting from catalog", folder.Path);
			}
		}

		return summaries
			.OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static async Task<string?> TryGetManifestAsync(HttpClient client, string folderPath, CancellationToken cancellationToken)
	{
		var requestUrl = $"repos/{RepoOwner}/{RepoName}/contents/{folderPath}/SKILL.md";
		using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
		// Request raw Markdown directly rather than the base64-encoded JSON wrapper — smaller
		// payload and skips a decode step.
		request.Headers.Accept.Clear();
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

		using var response = await client.SendAsync(request, cancellationToken);
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStringAsync(cancellationToken);
	}

	private async Task<string?> TryGetDefaultBranchShaAsync(HttpClient client, CancellationToken cancellationToken)
	{
		try
		{
			var repo = await GetAsync<GitHubRepository>(client, $"repos/{RepoOwner}/{RepoName}", cancellationToken);
			if (repo?.DefaultBranch is null)
			{
				return null;
			}

			var branch = await GetAsync<GitHubBranchRef>(
				client,
				$"repos/{RepoOwner}/{RepoName}/branches/{repo.DefaultBranch}",
				cancellationToken);
			return branch?.Commit?.Sha;
		}
		catch (Exception ex) when (ex is HttpRequestException or JsonException)
		{
			_logger.LogWarning(ex, "Failed to resolve default branch SHA for {Owner}/{Repo}; using HEAD ref", RepoOwner, RepoName);
			return null;
		}
	}

	private static async Task<T?> GetAsync<T>(HttpClient client, string relativeUrl, CancellationToken cancellationToken)
	{
		using var response = await client.GetAsync(relativeUrl, cancellationToken);
		response.EnsureSuccessStatusCode();
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
	}

	private async Task<HttpClient> CreateClientAsync(CancellationToken cancellationToken)
	{
		var client = _httpClientFactory.CreateClient(HttpClientName);
		if (client.DefaultRequestHeaders.Authorization is null)
		{
			var token = await TryResolveTokenAsync(cancellationToken);
			if (!string.IsNullOrWhiteSpace(token))
			{
				client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
			}
		}

		return client;
	}

	private async Task<string?> TryResolveTokenAsync(CancellationToken cancellationToken)
	{
		try
		{
			var settings = await _settingsService.GetSettingsAsync(cancellationToken);
			return settings?.GitHubToken;
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to read GitHub token from AppSettings; continuing unauthenticated");
			return null;
		}
	}

	// --- REST API DTOs (internal to this client) --------------------------------------

	private sealed class GitHubContentItem
	{
		[JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
		[JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
		[JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
		[JsonPropertyName("sha")] public string? Sha { get; set; }
	}

	private sealed class GitHubRepository
	{
		[JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
	}

	private sealed class GitHubBranchRef
	{
		[JsonPropertyName("commit")] public GitHubCommit? Commit { get; set; }
	}

	private sealed class GitHubCommit
	{
		[JsonPropertyName("sha")] public string? Sha { get; set; }
	}
}
