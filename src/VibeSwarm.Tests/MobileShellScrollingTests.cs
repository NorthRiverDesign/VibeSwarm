using System.Text.RegularExpressions;

namespace VibeSwarm.Tests;

public sealed class MobileShellScrollingTests
{
	[Fact]
	public void MainLayout_UsesAppOwnedScrollContainer()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));

		Assert.Contains("app-layout d-flex flex-column", layoutMarkup);
		Assert.DoesNotContain("min-vh-100", layoutMarkup);
		Assert.Contains("app-main flex-grow-1 min-height-0 overflow-y-auto overflow-x-hidden", layoutMarkup);
	}

	[Fact]
	public void MainLayout_UsesDashboardIconForMobileHeader_WithoutBrandBlock()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));

		Assert.Contains("title=\"Dashboard\" aria-label=\"Dashboard\"", layoutMarkup);
		Assert.Contains("<i class=\"bi bi-house-door\"></i>", layoutMarkup);
		Assert.Contains("ms-auto d-flex align-items-center gap-2", layoutMarkup);
		Assert.DoesNotContain("img/logo_icon.png", layoutMarkup);
		Assert.DoesNotContain("<span class=\"brand-text", layoutMarkup);
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
		Assert.Contains(".app-header .mobile-header-dropdown > .dropdown-menu", css);
		Assert.Contains("left: 50% !important;", css);
		Assert.Contains("transform: translateX(-50%) !important;", css);
		Assert.Contains("--vs-nav-popout-shadow:", css);
		Assert.Contains(".vs-nav-dropdown-menu", css);
		Assert.Contains("0 1.25rem 2.5rem rgba(15, 23, 42, 0.18)", css);
		Assert.Contains("box-shadow: var(--vs-nav-popout-shadow) !important;", css);
		Assert.Contains(".notifications-panel", css);
		Assert.Contains("box-shadow: var(--vs-nav-popout-shadow);", css);
		Assert.Contains("transform: translateX(-50%);", css);
		Assert.Contains("transform: translateX(-50%) translateY(-8px);", css);
		Assert.Contains("transform: translateX(-50%) translateY(0);", css);
	}

	[Fact]
	public void MobileHeaderDropdowns_UseStaticDisplayForViewportCenteredPanels()
	{
		var loginDisplayMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "LoginDisplay.razor"));
		var queuePanelMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Components", "Common", "QueueDropdownPanel.razor"));

		Assert.Contains("data-bs-toggle=\"dropdown\" data-bs-display=\"static\"", loginDisplayMarkup);
		Assert.Contains("data-bs-display=\"@(Compact ? \"static\" : null)\"", queuePanelMarkup);
		Assert.Contains("mobile-header-dropdown", loginDisplayMarkup);
		Assert.Contains("mobile-header-dropdown", queuePanelMarkup);
		Assert.Contains("vs-nav-dropdown-menu", loginDisplayMarkup);
		Assert.Contains("vs-nav-dropdown-menu", queuePanelMarkup);
	}

	[Fact]
	public void FullHeightScreens_UseViewportSafeHeightRules()
	{
		var appMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "App.razor"));
		var redirectMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Components", "Common", "RedirectToLogin.razor"));
		var css = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "css", "site.css"));
		var loginMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Web", "Pages", "Login.cshtml"));
		var setupMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Web", "Pages", "Setup.cshtml"));

		Assert.Contains("login-layout d-flex align-items-center justify-content-center", appMarkup);
		Assert.Contains("login-layout d-flex align-items-center justify-content-center", redirectMarkup);
		Assert.Contains("login-layout d-flex align-items-center justify-content-center", loginMarkup);
		Assert.Contains("login-layout d-flex align-items-center justify-content-center", setupMarkup);
		Assert.DoesNotContain("min-vh-100", appMarkup);
		Assert.DoesNotContain("min-vh-100", redirectMarkup);
		Assert.DoesNotContain("min-vh-100", loginMarkup);
		Assert.DoesNotContain("min-vh-100", setupMarkup);
		Assert.Matches(new Regex(@"\.login-layout\s*\{[\s\S]*box-sizing:\s*border-box;[\s\S]*min-height:\s*100vh;[\s\S]*min-height:\s*100dvh;", RegexOptions.Multiline), css);
		Assert.Matches(new Regex(@"\.app-layout\s*\{[\s\S]*min-height:\s*100vh;[\s\S]*min-height:\s*100dvh;[\s\S]*height:\s*100vh;[\s\S]*height:\s*100dvh;", RegexOptions.Multiline), css);
		Assert.Contains("100dvh - var(--vs-safe-area-top) - var(--vs-safe-area-bottom)", css);
		Assert.Matches(new Regex(@"@supports \(-webkit-touch-callout: none\)\s*\{[\s\S]*\.login-layout\s*\{[\s\S]*min-height:\s*-webkit-fill-available;", RegexOptions.Multiline), css);
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
