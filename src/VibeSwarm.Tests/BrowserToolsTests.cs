using VibeSwarm.Web.Controllers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class BrowserToolsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"vibeswarm-browser-locator-{Guid.NewGuid():N}");

	public BrowserToolsTests()
	{
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public void FindChromiumExecutable_PrefersNewestPlaywrightDownloadOverSystemChromium()
	{
		var browsersPath = Path.Combine(_root, "ms-playwright");
		var older = CreateFile(browsersPath, "chromium-1187", "chrome-linux", "chrome");
		var newest = CreateFile(browsersPath, "chromium-1248", "chrome-linux-arm64", "chrome");
		CreateFile(browsersPath, "chromium_headless_shell-1300", "chrome-headless-shell-linux-arm64", "chrome-headless-shell");
		var system = CreateFile(_root, "usr", "bin", "chromium");

		var found = BrowserToolsLocator.FindChromiumExecutable(browsersPath, [system]);

		Assert.Equal(newest, found);
		Assert.NotEqual(older, found);
	}

	[Fact]
	public void FindChromiumExecutable_SkipsRevisionsWithoutAnExecutable()
	{
		var browsersPath = Path.Combine(_root, "ms-playwright");
		Directory.CreateDirectory(Path.Combine(browsersPath, "chromium-1300"));
		var working = CreateFile(browsersPath, "chromium-1248", "chrome-linux64", "chrome");

		Assert.Equal(working, BrowserToolsLocator.FindChromiumExecutable(browsersPath, []));
	}

	[Fact]
	public void FindChromiumExecutable_FallsBackToFirstSystemChromiumThatExists()
	{
		var system = CreateFile(_root, "usr", "bin", "chromium");

		var found = BrowserToolsLocator.FindChromiumExecutable(
			Path.Combine(_root, "missing-cache"),
			[Path.Combine(_root, "usr", "bin", "google-chrome"), system]);

		Assert.Equal(system, found);
	}

	[Fact]
	public void FindChromiumExecutable_ReturnsNullWhenNothingIsInstalled()
	{
		Assert.Null(BrowserToolsLocator.FindChromiumExecutable(Path.Combine(_root, "missing-cache"), [Path.Combine(_root, "chromium")]));
	}

	[Fact]
	public void PlaywrightInstallCommand_InstallsChromiumAndFetchesTheMcpServer()
	{
		var command = SystemToolsController.GetPlaywrightInstallCommand();

		Assert.Contains("playwright@latest install", command);
		Assert.Contains("chromium", command);
		Assert.Contains("npx -y @playwright/mcp@latest --version", command);
		if (OperatingSystem.IsLinux())
		{
			// Without passwordless sudo, --with-deps would hang on a password prompt nobody sees.
			Assert.Contains("sudo -n true", command);
			Assert.Contains("command -v npx", command);
		}
	}

	private static string CreateFile(string root, params string[] parts)
	{
		var path = Path.Combine([root, .. parts]);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, string.Empty);
		return path;
	}
}
