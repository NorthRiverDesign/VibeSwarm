using System.Text.RegularExpressions;

namespace VibeSwarm.Tests;

public sealed class AppBrandIconTests
{
	[Fact]
	public void MainLayout_ShowsTheServedAppIconBesideTheNameInBothNavbars()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));
		var iconSources = Regex.Matches(layoutMarkup, @"<img src=""([^""]+)""[^>]*>\s*<span[^>]*>@AppBranding\.Name</span>")
			.Select(match => match.Groups[1].Value)
			.ToList();

		// The phone header and the desktop sidebar each show the icon next to the app name.
		Assert.Equal(2, iconSources.Count);
		foreach (var source in iconSources)
		{
			var iconPath = GetRepositoryPath(["src", "VibeSwarm.Client", "wwwroot", .. source.Split('/')]);
			Assert.True(File.Exists(iconPath), $"The navbar icon {source} is not in wwwroot.");
		}

		// The brand used to be an empty span painted with a gradient instead of the icon.
		Assert.DoesNotContain("app-brand-mark", layoutMarkup, StringComparison.Ordinal);
	}

	[Fact]
	public void SiteCss_PaintsNoGradientOverTheAppIcon()
	{
		var css = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "css", "site.css"));

		Assert.DoesNotContain(".app-brand-mark", css, StringComparison.Ordinal);
	}

	private static string GetRepositoryPath(params string[] segments)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VibeSwarm.sln")))
		{
			directory = directory.Parent;
		}

		Assert.NotNull(directory);
		return Path.Combine([directory.FullName, .. segments]);
	}
}
