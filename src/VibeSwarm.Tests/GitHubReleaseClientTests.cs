using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Web.Services.Updates;

namespace VibeSwarm.Tests;

public class GitHubReleaseClientTests
{
	private readonly UrlStubHttpHandler _http = new();

	private GitHubReleaseClient CreateClient() => new(_http, NullLogger<GitHubReleaseClient>.Instance);

	[Fact]
	public async Task GetLatestRelease_ReadsTheLatestRelease()
	{
		_http.Add("https://api.github.com/repos/owner/app/releases/latest", HttpStatusCode.OK, """
			{ "tag_name": "v1.4.0", "name": "1.4.0", "body": "Notes", "html_url": "https://github.com/owner/app/releases/tag/v1.4.0",
			  "draft": false, "prerelease": false, "published_at": "2026-11-01T10:00:00Z",
			  "assets": [ { "name": "vibeswarm-v1.4.0.tar.gz", "size": 10, "browser_download_url": "https://github.com/x", "digest": "sha256:ab" } ] }
			""");

		var lookup = await CreateClient().GetLatestReleaseAsync("owner/app", includePrereleases: false, CancellationToken.None);

		Assert.Null(lookup.Message);
		Assert.Equal("v1.4.0", lookup.Release!.TagName);
		Assert.Equal("sha256:ab", Assert.Single(lookup.Release.Assets).Digest);
	}

	[Fact]
	public async Task GetLatestRelease_WithPrereleases_PicksTheHighestVersionAndSkipsDrafts()
	{
		_http.Add("https://api.github.com/repos/owner/app/releases?per_page=20", HttpStatusCode.OK, """
			[ { "tag_name": "v1.4.0", "draft": false, "prerelease": false, "assets": [] },
			  { "tag_name": "v2.0.0", "draft": true, "prerelease": false, "assets": [] },
			  { "tag_name": "v1.5.0-beta.2", "draft": false, "prerelease": true, "assets": [] },
			  { "tag_name": "nightly", "draft": false, "prerelease": true, "assets": [] } ]
			""");

		var lookup = await CreateClient().GetLatestReleaseAsync("owner/app", includePrereleases: true, CancellationToken.None);

		Assert.Equal("v1.5.0-beta.2", lookup.Release!.TagName);
	}

	[Theory]
	[InlineData(HttpStatusCode.NotFound, "No releases have been published yet.")]
	[InlineData(HttpStatusCode.Forbidden, "limiting requests")]
	[InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
	[InlineData(HttpStatusCode.BadGateway, "502")]
	public async Task GetLatestRelease_ExplainsWhyThereIsNone(HttpStatusCode status, string expected)
	{
		_http.Add("https://api.github.com/repos/owner/app/releases/latest", status, "{}");

		var lookup = await CreateClient().GetLatestReleaseAsync("owner/app", includePrereleases: false, CancellationToken.None);

		Assert.Null(lookup.Release);
		Assert.Contains(expected, lookup.Message);
	}

	[Theory]
	[InlineData("")]
	[InlineData("owner")]
	[InlineData("owner/app/extra")]
	[InlineData("../etc/passwd")]
	[InlineData("owner/app?x=1")]
	public async Task GetLatestRelease_RefusesWhatIsNotARepository(string repository)
	{
		var lookup = await CreateClient().GetLatestReleaseAsync(repository, includePrereleases: false, CancellationToken.None);

		Assert.Null(lookup.Release);
		Assert.Empty(_http.Requests);
	}
}
