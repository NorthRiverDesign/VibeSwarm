using System.Text.RegularExpressions;

namespace VibeSwarm.Tests;

public sealed class MobileShellScrollingTests
{
	[Fact]
	public void MainLayout_UsesAppOwnedScrollContainer()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));

		Assert.Contains("app-main flex-grow-1 min-height-0 overflow-y-auto overflow-x-hidden", layoutMarkup);
	}

	[Fact]
	public void SiteCss_UsesMomentumScrollingForAppShell()
	{
		var css = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "css", "site.css"));

		Assert.Contains("body:has(.app-layout)", css);
		Assert.Contains("overflow: hidden;", css);
		Assert.Contains("-webkit-overflow-scrolling: touch;", css);
		Assert.Contains(".app-sidebar > .overflow-y-auto", css);
		Assert.Contains("height: -webkit-fill-available;", css);
		Assert.Matches(new Regex(@"\.app-layout\s*\{[\s\S]*height:\s*100dvh;", RegexOptions.Multiline), css);
		Assert.Contains("scroll-padding-top: calc(", css);
		Assert.Contains(".app-header .dropdown.d-lg-none > .dropdown-menu", css);
		Assert.Contains("left: 50% !important;", css);
		Assert.Contains("transform: translateX(-50%) !important;", css);
		Assert.Contains(".notifications-panel", css);
		Assert.Contains("transform: translateX(-50%);", css);
	}

	[Fact]
	public void MobileHeaderDropdowns_UseStaticDisplayForViewportCenteredPanels()
	{
		var loginDisplayMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "LoginDisplay.razor"));
		var queuePanelMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Components", "Common", "QueueDropdownPanel.razor"));

		Assert.Contains("data-bs-toggle=\"dropdown\" data-bs-display=\"static\"", loginDisplayMarkup);
		Assert.Contains("data-bs-display=\"@(Compact ? \"static\" : null)\"", queuePanelMarkup);
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
