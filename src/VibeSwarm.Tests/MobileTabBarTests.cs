using System.Text.RegularExpressions;

namespace VibeSwarm.Tests;

public sealed class MobileTabBarTests
{
	private static readonly string[] TabPaths = ["/", "/projects", "/jobs", "/more"];

	[Fact]
	public void MainLayout_NavigatesPhonesWithATabBarInsteadOfAMenuDrawer()
	{
		var layoutMarkup = ReadMainLayout();

		Assert.Contains("app-tab-bar d-flex lg:d-none", layoutMarkup, StringComparison.Ordinal);
		Assert.Contains("app-sidebar position-fixed d-none lg:d-flex", layoutMarkup, StringComparison.Ordinal);
		Assert.DoesNotContain("mobile-menu-toggle", layoutMarkup, StringComparison.Ordinal);
		Assert.Equal(TabPaths, GetTabBarHrefs(layoutMarkup));
	}

	[Fact]
	public void MorePage_ListsEverySidebarDestinationWithoutATab()
	{
		var layoutMarkup = ReadMainLayout();
		var moreMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Pages", "More.razor"));
		var sidebarHrefs = Regex.Matches(layoutMarkup, @"<NavLink class=""nav-item"" href=""([^""]+)""")
			.Select(match => match.Groups[1].Value)
			.ToList();

		Assert.NotEmpty(sidebarHrefs);
		foreach (var href in sidebarHrefs.Except(TabPaths))
		{
			// Otherwise the page is out of reach on a phone, and the More tab wouldn't light up on it.
			Assert.Contains($@"Href=""{href}""", moreMarkup, StringComparison.Ordinal);
			Assert.Contains($@"""{href}""", GetMoreTabPaths(layoutMarkup), StringComparison.Ordinal);
		}
	}

	[Fact]
	public void MainLayout_ResetsPageScrollThroughTheIndexScript()
	{
		var layoutMarkup = ReadMainLayout();
		var indexHtml = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "index.html"));

		Assert.Contains(@"InvokeVoidAsync(""vibeSwarmPageScroll.pageChanged"")", layoutMarkup, StringComparison.Ordinal);
		Assert.Contains("window.vibeSwarmPageScroll = ", indexHtml, StringComparison.Ordinal);
		Assert.Contains("pageChanged: function ()", indexHtml, StringComparison.Ordinal);
	}

	private static List<string> GetTabBarHrefs(string layoutMarkup)
	{
		var tabBar = Regex.Match(layoutMarkup, @"<nav class=""app-tab-bar.*?</nav>", RegexOptions.Singleline);
		Assert.True(tabBar.Success);

		return Regex.Matches(tabBar.Value, @"href=""([^""]+)""")
			.Select(match => match.Groups[1].Value)
			.ToList();
	}

	private static string GetMoreTabPaths(string layoutMarkup)
	{
		var paths = Regex.Match(layoutMarkup, @"MoreTabPaths\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
		Assert.True(paths.Success);
		return paths.Groups[1].Value;
	}

	private static string ReadMainLayout()
		=> File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));

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
