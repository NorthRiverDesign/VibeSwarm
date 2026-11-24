using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Branding;

namespace VibeSwarm.Web.Services.Updates;

public interface IGitHubReleaseClient
{
	/// <summary>
	/// The newest published release of <paramref name="repository"/>, skipping drafts, and
	/// pre-releases unless <paramref name="includePrereleases"/>.
	/// </summary>
	Task<ReleaseLookup> GetLatestReleaseAsync(string repository, bool includePrereleases, CancellationToken cancellationToken);
}

/// <summary>A release, or why none was found ("no releases yet", offline, rate-limited).</summary>
public sealed record ReleaseLookup(GitHubRelease? Release, string? Message);

public sealed class GitHubRelease
{
	[JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
	[JsonPropertyName("name")] public string? Name { get; set; }
	[JsonPropertyName("body")] public string? Body { get; set; }
	[JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
	[JsonPropertyName("draft")] public bool Draft { get; set; }
	[JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
	[JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
	[JsonPropertyName("assets")] public List<GitHubReleaseAsset> Assets { get; set; } = [];
}

public sealed class GitHubReleaseAsset
{
	[JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
	[JsonPropertyName("size")] public long Size { get; set; }
	[JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;

	/// <summary><c>sha256:&lt;hex&gt;</c>, which GitHub computes for every asset uploaded since mid-2025.</summary>
	[JsonPropertyName("digest")] public string? Digest { get; set; }
}

public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
	public const string HttpClientName = "AppUpdates";

	private readonly IHttpClientFactory _httpClientFactory;
	private readonly ILogger<GitHubReleaseClient> _logger;

	public GitHubReleaseClient(IHttpClientFactory httpClientFactory, ILogger<GitHubReleaseClient> logger)
	{
		_httpClientFactory = httpClientFactory;
		_logger = logger;
	}

	/// <summary>A User-Agent that names the app and its version, and nothing about the host or user.</summary>
	public static void ConfigureClient(HttpClient client)
	{
		client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"{AppBranding.PackageId}-updater", AppVersionInfo.Current.ToString()));
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
		client.Timeout = TimeSpan.FromSeconds(30);
	}

	public static bool IsValidRepository(string? repository) =>
		!string.IsNullOrWhiteSpace(repository) &&
		repository.Split('/') is [var owner, var name] &&
		owner.Length > 0 && name.Length > 0 &&
		repository.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.');

	public async Task<ReleaseLookup> GetLatestReleaseAsync(string repository, bool includePrereleases, CancellationToken cancellationToken)
	{
		if (!IsValidRepository(repository))
		{
			return new ReleaseLookup(null, $"'{repository}' isn't a GitHub repository (owner/name).");
		}

		var client = _httpClientFactory.CreateClient(HttpClientName);
		var path = includePrereleases
			? $"repos/{repository}/releases?per_page=20"
			: $"repos/{repository}/releases/latest";

		try
		{
			using var response = await client.GetAsync(path, cancellationToken);
			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return new ReleaseLookup(null, "No releases have been published yet.");
			}

			if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
			{
				return new ReleaseLookup(null, "GitHub is limiting requests from this network. Try again in an hour.");
			}

			if (!response.IsSuccessStatusCode)
			{
				return new ReleaseLookup(null, $"GitHub answered {(int)response.StatusCode}. Try again later.");
			}

			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			if (!includePrereleases)
			{
				var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken);
				return release is null || release.Draft
					? new ReleaseLookup(null, "No releases have been published yet.")
					: new ReleaseLookup(release, null);
			}

			var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, cancellationToken: cancellationToken) ?? [];
			var newest = releases
				.Where(r => !r.Draft)
				.Select(r => (Release: r, Version: ReleaseVersion.TryParse(r.TagName, out var v) ? v : null))
				.Where(r => r.Version is not null)
				.OrderByDescending(r => r.Version)
				.Select(r => r.Release)
				.FirstOrDefault();
			return newest is null
				? new ReleaseLookup(null, "No releases have been published yet.")
				: new ReleaseLookup(newest, null);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new ReleaseLookup(null, "GitHub didn't answer in time. Check this host's internet connection.");
		}
		catch (HttpRequestException ex)
		{
			_logger.LogInformation("Couldn't reach GitHub to check for updates: {Message}", ex.Message);
			return new ReleaseLookup(null, "Couldn't reach GitHub. Check this host's internet connection.");
		}
		catch (JsonException ex)
		{
			_logger.LogWarning(ex, "GitHub's release list couldn't be read.");
			return new ReleaseLookup(null, "GitHub's answer couldn't be read. Try again later.");
		}
	}
}
