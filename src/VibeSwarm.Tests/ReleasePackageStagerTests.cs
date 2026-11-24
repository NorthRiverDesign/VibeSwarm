using System.Formats.Tar;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Web.Services.Updates;

namespace VibeSwarm.Tests;

public sealed class ReleasePackageStagerTests : IDisposable
{
	private const string PackageUrl = "https://github.com/owner/app/releases/download/v0.0.0-dev/vibeswarm-v0.0.0-dev.tar.gz";
	private const string SumsUrl = "https://github.com/owner/app/releases/download/v0.0.0-dev/SHA256SUMS";

	// The packaged dll is this build's VibeSwarm.Web.dll: 0.0.0-dev, or the tag's version in release.yml.
	private static readonly ReleaseVersion PackagedVersion = ReleaseVersion.TryParse(
		System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "VibeSwarm.Web.dll")).ProductVersion,
		out var version) ? version : throw new InvalidOperationException("VibeSwarm.Web.dll has no version.");

	// The running release's folder; named so no stamped version can collide with it.
	private const string RunningFolder = "0.0.0-running";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"stager-{Guid.NewGuid():N}");
	private readonly UrlStubHttpHandler _http = new();
	private readonly FakeRuntimes _runtimes = new();
	private readonly InstallLayout _layout;

	public ReleasePackageStagerTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "versions", RunningFolder));
		File.WriteAllText(Path.Combine(_root, InstallLayout.LauncherFileName), "#!/usr/bin/env bash\n");
		_layout = InstallLayout.TryResolve(_root, Path.Combine(_root, "versions", RunningFolder), out var reason)
			?? throw new InvalidOperationException(reason);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private ReleasePackageStager CreateStager() => new(_http, _runtimes, NullLogger<ReleasePackageStager>.Instance);

	private static GitHubRelease Release(byte[] package, string? digest, bool withSums = false)
	{
		var release = new GitHubRelease
		{
			TagName = $"v{PackagedVersion}",
			Assets =
			[
				new GitHubReleaseAsset { Name = "vibeswarm-v0.0.0-dev.tar.gz", Size = package.Length, BrowserDownloadUrl = PackageUrl, Digest = digest }
			]
		};
		if (withSums)
		{
			release.Assets.Add(new GitHubReleaseAsset { Name = ReleasePackageStager.ChecksumFileName, Size = 100, BrowserDownloadUrl = SumsUrl });
		}

		return release;
	}

	private IEnumerable<string> VersionFolders() => Directory.EnumerateDirectories(_layout.VersionsDirectory).Select(Path.GetFileName)!;

	[Fact]
	public async Task StageAsync_VerifiesAndMovesThePackageIntoItsVersionFolder()
	{
		var package = ReleasePackageBuilder.Build();
		_http.Add(PackageUrl, package);
		var progress = new List<int>();

		var staged = await CreateStager().StageAsync(Release(package, $"sha256:{ReleasePackageBuilder.Sha256(package)}"),
			PackagedVersion, _layout, new SynchronousProgress(progress.Add), CancellationToken.None);

		Assert.Equal(_layout.GetVersionDirectory(PackagedVersion.ToString()), staged);
		Assert.True(File.Exists(Path.Combine(staged, "VibeSwarm.Web.dll")));
		Assert.True(File.Exists(Path.Combine(staged, "Deploy", "launcher.sh")));
		Assert.Equal(new[] { PackagedVersion.ToString(), RunningFolder }.Order(), VersionFolders().Order());
		Assert.Equal(100, progress[^1]);
	}

	[Fact]
	public async Task StageAsync_FallsBackToSha256Sums()
	{
		var package = ReleasePackageBuilder.Build();
		_http.Add(PackageUrl, package);
		_http.Add(SumsUrl, System.Text.Encoding.UTF8.GetBytes(
			$"{new string('0', 64)}  vibeswarm-v0.0.0-dev.zip\n{ReleasePackageBuilder.Sha256(package)}  vibeswarm-v0.0.0-dev.tar.gz\n"));

		var staged = await CreateStager().StageAsync(Release(package, digest: null, withSums: true), PackagedVersion, _layout, null, CancellationToken.None);

		Assert.True(Directory.Exists(staged));
	}

	[Fact]
	public async Task StageAsync_RefusesAPackageWhoseChecksumDoesNotMatch_AndLeavesNothingBehind()
	{
		var package = ReleasePackageBuilder.Build();
		_http.Add(PackageUrl, package);

		var error = await Assert.ThrowsAsync<UpdateException>(() => CreateStager().StageAsync(
			Release(package, $"sha256:{new string('a', 64)}"), PackagedVersion, _layout, null, CancellationToken.None));

		Assert.Contains("SHA-256", error.Message);
		Assert.Equal([RunningFolder], VersionFolders());
	}

	[Fact]
	public async Task StageAsync_RefusesAReleaseWithoutAChecksum()
	{
		var package = ReleasePackageBuilder.Build();
		_http.Add(PackageUrl, package);

		var error = await Assert.ThrowsAsync<UpdateException>(() => CreateStager().StageAsync(
			Release(package, digest: null), PackagedVersion, _layout, null, CancellationToken.None));

		Assert.Contains("no checksum", error.Message);
		Assert.Empty(_http.Requests);
	}

	[Fact]
	public async Task StageAsync_RefusesADownloadLinkOffGitHub()
	{
		var package = ReleasePackageBuilder.Build();
		var release = Release(package, $"sha256:{ReleasePackageBuilder.Sha256(package)}");
		release.Assets[0].BrowserDownloadUrl = "http://example.com/vibeswarm-v0.0.0-dev.tar.gz";

		await Assert.ThrowsAsync<UpdateException>(() => CreateStager().StageAsync(release, PackagedVersion, _layout, null, CancellationToken.None));
		Assert.Empty(_http.Requests);
	}

	[Fact]
	public async Task StageAsync_RefusesEntriesThatEscapeTheFolder()
	{
		var package = ReleasePackageBuilder.Build(tar => ReleasePackageBuilder.AddText(tar, "vibeswarm/../../escaped.txt", "x"));
		_http.Add(PackageUrl, package);

		var error = await Assert.ThrowsAsync<UpdateException>(() => CreateStager().StageAsync(
			Release(package, $"sha256:{ReleasePackageBuilder.Sha256(package)}"), PackagedVersion, _layout, null, CancellationToken.None));

		Assert.Contains("outside", error.Message);
		Assert.False(File.Exists(Path.Combine(_layout.VersionsDirectory, "escaped.txt")));
		Assert.Equal([RunningFolder], VersionFolders());
	}

	[Fact]
	public async Task StageAsync_RefusesLinks()
	{
		var package = ReleasePackageBuilder.Build(tar =>
			tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "vibeswarm/passwd") { LinkName = "/etc/passwd" }));
		_http.Add(PackageUrl, package);

		var error = await Assert.ThrowsAsync<UpdateException>(() => CreateStager().StageAsync(
			Release(package, $"sha256:{ReleasePackageBuilder.Sha256(package)}"), PackagedVersion, _layout, null, CancellationToken.None));

		Assert.Contains("SymbolicLink", error.Message);
	}

	[Fact]
	public async Task StageAsync_RefusesAPackageOfAnotherVersion()
	{
		var package = ReleasePackageBuilder.Build();
		_http.Add(PackageUrl, package);

		var error = await Assert.ThrowsAsync<UpdateException>(() => CreateStager().StageAsync(
			Release(package, $"sha256:{ReleasePackageBuilder.Sha256(package)}"), new ReleaseVersion(9, 9, 9, null), _layout, null, CancellationToken.None));

		Assert.Contains("not 9.9.9", error.Message);
		Assert.Equal([RunningFolder], VersionFolders());
	}

	[Fact]
	public async Task StageAsync_RefusesARuntimeThisHostLacks()
	{
		var package = ReleasePackageBuilder.Build(runtimeConfig: ReleasePackageBuilder.RuntimeConfig.Replace("10.0.0", "11.0.0"));
		_http.Add(PackageUrl, package);

		var error = await Assert.ThrowsAsync<UpdateException>(() => CreateStager().StageAsync(
			Release(package, $"sha256:{ReleasePackageBuilder.Sha256(package)}"), PackagedVersion, _layout, null, CancellationToken.None));

		Assert.Contains("Microsoft.NETCore.App 11.0", error.Message);
		Assert.Equal([RunningFolder], VersionFolders());
	}

	[Theory]
	[InlineData("10.0.5", "10.0.0", true)]
	[InlineData("10.1.0", "10.0.3", true)]
	[InlineData("10.0.2", "10.0.3", false)]
	[InlineData("11.0.0", "10.0.0", false)]
	[InlineData("10.0.0-rc.1", "10.0.0", false)]
	public void SatisfiesRollForward_MatchesTheDefaultPolicy(string installed, string required, bool expected)
	{
		ReleaseVersion.TryParse(installed, out var installedVersion);
		ReleaseVersion.TryParse(required, out var requiredVersion);

		Assert.Equal(expected, ReleasePackageStager.SatisfiesRollForward(installedVersion!, requiredVersion!));
	}

	[Fact]
	public void ParseListRuntimes_ReadsDotnetOutput()
	{
		var runtimes = DotnetInstalledRuntimeProvider.ParseListRuntimes("""
			Microsoft.AspNetCore.App 10.0.5 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
			Microsoft.NETCore.App 10.0.5 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
			""");

		Assert.Equal(2, runtimes.Count);
		Assert.Equal(("Microsoft.AspNetCore.App", new ReleaseVersion(10, 0, 5, null)), runtimes[0]);
	}

	private sealed class FakeRuntimes : IInstalledRuntimeProvider
	{
		public Task<IReadOnlyList<(string Name, ReleaseVersion Version)>?> GetInstalledRuntimesAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<(string Name, ReleaseVersion Version)>?>(
			[
				("Microsoft.NETCore.App", new ReleaseVersion(10, 0, 5, null)),
				("Microsoft.AspNetCore.App", new ReleaseVersion(10, 0, 5, null))
			]);
	}

	private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
	{
		public void Report(int value) => report(value);
	}
}
