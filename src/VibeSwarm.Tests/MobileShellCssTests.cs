using System.Text.RegularExpressions;

namespace VibeSwarm.Tests;

public sealed class MobileShellCssTests
{
	[Fact]
	public void SiteCss_LetsIosSizeTheShellWithDynamicViewportHeight()
	{
		var css = ReadSiteCss();

		// Only iOS Safari applies -webkit-fill-available. Declared after the 100dvh rules, it
		// replaced them on every iPhone and left an empty strip along the bottom of the screen.
		Assert.DoesNotContain("-webkit-fill-available", css);
		Assert.Matches(new Regex(@"\.app-layout\s*\{[^}]*height:\s*100dvh;"), css);
	}

	[Fact]
	public void SiteCss_ClearsTheHomeIndicatorOnlyAtTheBottomEdgeOfTheScreen()
	{
		var css = ReadSiteCss();

		Assert.Contains("--vs-safe-area-bottom: env(safe-area-inset-bottom, 0px);", css);
		// On phones the tab bar is the bottom band, so it clears the home indicator and the
		// page content above it doesn't add the inset a second time.
		Assert.Matches(new Regex(@"\.app-tab-bar\s*\{[^}]*padding-bottom:\s*var\(--vs-safe-area-bottom\);"), css);
		Assert.DoesNotMatch(new Regex(@"\.main-content\s*\{[^}]*safe-area-bottom"), css);
		// A full-screen dialog pads whichever band is last, never the body and the footer both.
		Assert.Matches(new Regex(@"\.dialog \.dialog-body:last-child\s*\{[^}]*max\("), css);
		Assert.Matches(new Regex(@"\.dialog \.dialog-footer\s*\{[^}]*padding-bottom:\s*max\(0\.75rem, var\(--vs-safe-area-bottom\)\);"), css);
	}

	[Fact]
	public void SiteCss_KeepsIosFromZoomingIntoSmallFields()
	{
		var css = ReadSiteCss();

		// iOS zooms the page into any focused field under 16px, small fields included; small
		// buttons keep their small text.
		Assert.Matches(new Regex(@"@media \(pointer: coarse\)\s*\{[^@]*\.form-control\s*\{\s*--bs-control-font-size: var\(--vs-text-body\);"), css);
		Assert.DoesNotContain("--bs-btn-input-sm-font-size", css);
	}

	[Fact]
	public void FloatingBanners_SitAboveTheTabBar()
	{
		foreach (var banner in new[] { "AppUpdateBanner.razor", "InstallPromptBanner.razor" })
		{
			var markup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Components", "Common", banner));

			Assert.Contains("app-floating-banner", markup);
			Assert.DoesNotContain("bottom-0", markup);
		}

		Assert.Matches(new Regex(@"\.app-floating-banner\s*\{[^}]*bottom:\s*calc\(var\(--vs-tab-bar-height\) \+ var\(--vs-safe-area-bottom\)\);"), ReadSiteCss());
	}

	[Fact]
	public void SiteCss_DrawsNoFocusRingRoundThePageHeadingAfterNavigation()
	{
		// FocusOnNavigate focuses each new page's <h1> so screen readers announce it; iOS drew
		// a focus ring round the heading after every tab switch.
		Assert.Contains("<FocusOnNavigate", File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "App.razor")));
		Assert.Matches(new Regex(@"h1:focus\s*\{\s*outline:\s*none;"), ReadSiteCss());
	}

	[Fact]
	public void ComposerFields_GrowWithTheirText()
	{
		Assert.Matches(new Regex(@"textarea\.textarea-autogrow\s*\{[^}]*field-sizing:\s*content;[^}]*max-height:\s*40dvh;"), ReadSiteCss());

		// Browsers without field-sizing are sized from index.html, which also catches the value
		// Blazor sets when it empties a field after sending.
		var index = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "index.html"));
		Assert.Contains("CSS.supports('field-sizing', 'content')", index);
		Assert.Contains("getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')", index);

		var ideasPanel = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Components", "Ideas", "IdeasPanel.razor"));
		Assert.Matches(new Regex(@"<textarea class=""[^""]*\btextarea-autogrow\b[^""]*""[^>]*aria-label=""New idea"""), ideasPanel);

		foreach (var composer in new[]
		{
			new[] { "Ideas", "IdeaListItem.razor" },
			new[] { "Jobs", "JobSessionPanel.razor" },
			new[] { "Jobs", "CreateJobModal.razor" },
			new[] { "Jobs", "EditJobModal.razor" },
			new[] { "Projects", "ProjectChangesTab.razor" },
		})
		{
			var markup = File.ReadAllText(GetRepositoryPath(["src", "VibeSwarm.Client", "Components", .. composer]));
			Assert.Contains("textarea-autogrow", markup);
		}
	}

	[Fact]
	public void GlobalSearch_ShowsKeyboardHintsOnlyOnDesktop()
	{
		var markup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Components", "Common", "GlobalSearchModal.razor"));

		Assert.Matches(new Regex(@"<div class=""[^""]*\bd-none lg:d-flex\b[^""]*"">\s*<span[^>]*>\s*<kbd[^>]*>↑↓</kbd> navigate"), markup);
	}

	private static string ReadSiteCss()
		=> File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "css", "site.css"));

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
