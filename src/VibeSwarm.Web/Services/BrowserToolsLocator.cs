using System.Text.RegularExpressions;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Finds the Chromium that agents drive through Playwright MCP. Playwright's own download
/// (Settings > Browser for agents installs it into the shared browser cache) wins over a system
/// Chromium, which is the reliable choice on ARM hosts where Google Chrome isn't published.
/// </summary>
public static partial class BrowserToolsLocator
{
	// Playwright 1.57+ downloads Chrome for Testing builds; older releases used chrome-linux and friends.
	private static readonly string[][] PlaywrightChromiumLayouts =
	[
		["chrome-linux64", "chrome"],
		["chrome-linux-arm64", "chrome"],
		["chrome-linux", "chrome"],
		["chrome-mac-arm64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"],
		["chrome-mac-x64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"],
		["chrome-mac", "Chromium.app", "Contents", "MacOS", "Chromium"],
		["chrome-win64", "chrome.exe"],
		["chrome-win", "chrome.exe"]
	];

	/// <summary>
	/// The directory Playwright installs browsers into for the user VibeSwarm runs as. Jobs point
	/// <c>PLAYWRIGHT_BROWSERS_PATH</c> here so they share one download instead of fetching their own.
	/// </summary>
	public static string GetPlaywrightBrowsersPath()
	{
		var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
		if (!string.IsNullOrWhiteSpace(configured) && configured != "0")
		{
			return configured;
		}

		if (OperatingSystem.IsWindows())
		{
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
		}

		var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (OperatingSystem.IsMacOS())
		{
			return Path.Combine(home, "Library", "Caches", "ms-playwright");
		}

		var cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
		return Path.Combine(string.IsNullOrWhiteSpace(cacheHome) ? Path.Combine(home, ".cache") : cacheHome, "ms-playwright");
	}

	/// <summary>The Chromium executable agents can launch, or null when none is installed.</summary>
	public static string? FindChromiumExecutable() =>
		FindChromiumExecutable(GetPlaywrightBrowsersPath(), GetSystemChromiumCandidates());

	/// <summary>
	/// The Chromium a job's Playwright MCP server launches, or null when the job can't have a
	/// browser: Playwright MCP runs through <c>npx</c>, so Node.js has to be installed too.
	/// </summary>
	public static string? FindChromiumForJobs()
	{
		var npx = PlatformHelper.ResolveExecutablePath("npx", searchPath: PlatformHelper.GetEnhancedPath());
		return Path.IsPathRooted(npx) ? FindChromiumExecutable() : null;
	}

	internal static string? FindChromiumExecutable(string browsersPath, IEnumerable<string> systemCandidates)
	{
		return FindPlaywrightChromium(browsersPath) ?? systemCandidates.FirstOrDefault(File.Exists);
	}

	private static string? FindPlaywrightChromium(string browsersPath)
	{
		try
		{
			if (!Directory.Exists(browsersPath))
			{
				return null;
			}

			var revisions = Directory.GetDirectories(browsersPath)
				.Select(directory => (Directory: directory, Match: ChromiumRevisionPattern().Match(Path.GetFileName(directory))))
				.Where(candidate => candidate.Match.Success)
				.OrderByDescending(candidate => long.TryParse(candidate.Match.Groups[1].Value, out var revision) ? revision : 0);

			foreach (var (directory, _) in revisions)
			{
				foreach (var layout in PlaywrightChromiumLayouts)
				{
					var executable = Path.Combine([directory, .. layout]);
					if (File.Exists(executable))
					{
						return executable;
					}
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// An unreadable cache counts as no download; a system Chromium may still be there.
		}

		return null;
	}

	private static IEnumerable<string> GetSystemChromiumCandidates()
	{
		if (OperatingSystem.IsWindows())
		{
			foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
			{
				yield return Path.Combine(Environment.GetFolderPath(root), "Google", "Chrome", "Application", "chrome.exe");
			}

			yield break;
		}

		if (OperatingSystem.IsMacOS())
		{
			yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
			yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
			yield break;
		}

		yield return "/usr/bin/chromium";
		yield return "/usr/bin/chromium-browser";
		yield return "/usr/bin/google-chrome-stable";
		yield return "/usr/bin/google-chrome";
	}

	[GeneratedRegex(@"^chromium-(\d+)$")]
	private static partial Regex ChromiumRevisionPattern();
}
