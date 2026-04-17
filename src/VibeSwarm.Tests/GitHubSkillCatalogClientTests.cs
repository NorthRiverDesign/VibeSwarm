using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class GitHubSkillCatalogClientTests
{
	[Fact]
	public async Task ListSkillsAsync_ParsesFolderContentsAndCachesResult()
	{
		var handler = new CountingHandler();
		// Repo metadata → used to resolve the default branch name.
		handler.EnqueueJson("""{"default_branch":"main"}""");
		// Default branch → provides a SHA stamped onto each catalog entry.
		handler.EnqueueJson("""{"name":"main","commit":{"sha":"abc1234"}}""");
		// Contents of the repo root.
		handler.EnqueueJson(
			"""
			[
				{"name":"pdf","path":"pdf","type":"dir"},
				{"name":"docs","path":"docs","type":"dir"},
				{"name":"README.md","path":"README.md","type":"file"}
			]
			""");
		// SKILL.md for pdf folder.
		handler.EnqueueRaw(
			"""
			---
			name: pdf
			description: Extract text from PDFs.
			allowed-tools: Bash(python:*)
			---

			# PDF Skill
			""");
		// The `docs/` folder has no SKILL.md → 404 → quietly skipped.
		handler.EnqueueStatus(HttpStatusCode.NotFound);

		var client = CreateClient(handler, out var cache);

		var first = await client.ListSkillsAsync();
		var second = await client.ListSkillsAsync();

		Assert.Same(first, second); // Second call must return the same cached instance.
		var pdf = Assert.Single(first);
		Assert.Equal("pdf", pdf.Slug);
		Assert.Equal("pdf", pdf.Name);
		Assert.Equal("Extract text from PDFs.", pdf.Description);
		Assert.Equal("Bash(python:*)", pdf.AllowedTools);
		Assert.Equal("abc1234", pdf.Ref);

		// 5 HTTP calls for the first catalog load, 0 for the cached second call.
		Assert.Equal(5, handler.RequestCount);
	}

	[Fact]
	public async Task InvalidateCache_ForcesReloadOnNextCall()
	{
		var handler = new CountingHandler();
		QueueEmptyCatalogResponses(handler);
		QueueEmptyCatalogResponses(handler);

		var client = CreateClient(handler, out _);

		_ = await client.ListSkillsAsync();
		client.InvalidateCache();
		_ = await client.ListSkillsAsync();

		// Each "empty catalog" round is 3 requests (repo + branch + root contents).
		Assert.Equal(6, handler.RequestCount);
	}

	[Fact]
	public async Task ListSkillsAsync_AttachesBearerToken_WhenAppSettingsHasOne()
	{
		var handler = new CountingHandler();
		QueueEmptyCatalogResponses(handler);

		var settings = new FakeSettingsService(new AppSettings { GitHubToken = "ghp_testtoken" });
		var client = CreateClient(handler, out _, settings);

		await client.ListSkillsAsync();

		// DefaultRequestHeaders.Authorization rides on every outbound call — assert every
		// captured request carried the token rather than counting exactly one.
		Assert.NotEmpty(handler.CapturedAuthorizations);
		Assert.All(handler.CapturedAuthorizations, auth =>
		{
			Assert.Equal("Bearer", auth.Scheme);
			Assert.Equal("ghp_testtoken", auth.Parameter);
		});
	}

	private static GitHubSkillCatalogClient CreateClient(
		HttpMessageHandler handler,
		out IMemoryCache cache,
		ISettingsService? settingsService = null)
	{
		cache = new MemoryCache(new MemoryCacheOptions());
		var httpClient = new HttpClient(handler)
		{
			BaseAddress = new Uri("https://api.github.com/"),
		};
		var factory = new SingleClientHttpClientFactory(httpClient);
		return new GitHubSkillCatalogClient(
			factory,
			cache,
			settingsService ?? new FakeSettingsService(new AppSettings()),
			NullLogger<GitHubSkillCatalogClient>.Instance);
	}

	private static void QueueEmptyCatalogResponses(CountingHandler handler)
	{
		handler.EnqueueJson("""{"default_branch":"main"}""");
		handler.EnqueueJson("""{"name":"main","commit":{"sha":"sha1"}}""");
		handler.EnqueueJson("[]");
	}

	private sealed class CountingHandler : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> _responses = new();
		public int RequestCount { get; private set; }
		public List<System.Net.Http.Headers.AuthenticationHeaderValue> CapturedAuthorizations { get; } = [];

		public void EnqueueJson(string json) =>
			_responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(json, Encoding.UTF8, "application/json"),
			});

		public void EnqueueRaw(string body) =>
			_responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "text/plain"),
			});

		public void EnqueueStatus(HttpStatusCode status) =>
			_responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent("") });

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestCount++;
			if (request.Headers.Authorization is not null)
			{
				CapturedAuthorizations.Add(request.Headers.Authorization);
			}

			if (_responses.Count == 0)
			{
				throw new InvalidOperationException("Unexpected additional request: " + request.RequestUri);
			}

			return Task.FromResult(_responses.Dequeue());
		}
	}

	private sealed class SingleClientHttpClientFactory : IHttpClientFactory
	{
		private readonly HttpClient _client;
		public SingleClientHttpClientFactory(HttpClient client) => _client = client;
		public HttpClient CreateClient(string name) => _client;
	}

	private sealed class FakeSettingsService : ISettingsService
	{
		private readonly AppSettings _settings;
		public FakeSettingsService(AppSettings settings) => _settings = settings;

		public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);
		public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings.DefaultProjectsDirectory);
	}
}
