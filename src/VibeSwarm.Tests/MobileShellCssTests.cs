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
	public void SiteCss_SizesTheHomeScreenAppShellToTheWholeScreen()
	{
		var standalone = Regex.Match(ReadSiteCss(), @"@media all and \(display-mode: standalone\)\s*\{(?<body>(?:[^{}]|\{[^{}]*\})*)\}");
		Assert.True(standalone.Success);

		// A Home Screen app sometimes works out dvh without the status bar, which lifted the
		// tab bar off the bottom of the screen; there 100vh is always the whole screen.
		var rules = standalone.Groups["body"].Value;
		Assert.Matches(new Regex(@"(?<![-\w])html\s*\{[^}]*height:\s*100vh;"), rules);
		Assert.Matches(new Regex(@"body:has\(\.app-layout\),\s*\.app-layout\s*\{[^}]*min-height:\s*100vh;[^}]*height:\s*100vh;"), rules);
	}

	[Fact]
	public void LiveTranscript_FollowsToTheRealEndOfItsScroller()
	{
		var index = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "index.html"));
		var sync = Regex.Match(index, @"sync: function \(element\) \{[\s\S]*?live\.showJumpButton\(element, state\);\s*\}\s*\};");
		Assert.True(sync.Success);

		// When the transcript shrinks under it, iOS can leave the page scrolled past the last
		// message; following puts it back and never aims past the end.
		Assert.Contains("var end = Math.max(0, scroller.scrollHeight - scroller.clientHeight);", sync.Value);
		Assert.Matches(new Regex(@"if \(scroller\.scrollTop > end\) \{\s*scroller\.scrollTop = end;"), sync.Value);
		Assert.Matches(new Regex(@"scroller\.scrollTo\(\{\s*top: end,"), sync.Value);
		Assert.DoesNotContain("top: scroller.scrollHeight,", index);
		Assert.DoesNotContain("top: state.scroller.scrollHeight,", index);
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

	[Fact]
	public void SiteCss_SlidesHeaderPanelsWithTranslateSoPhoneCentringSurvives()
	{
		var css = ReadSiteCss();

		// On phones the header panels are centred with `transform: translateX(-50%) !important`,
		// so a transform slide never moves there. The standalone translate property composes
		// with it, and display stays in the list so the panel fades out before it is hidden.
		var panel = Regex.Match(css, @"\.menu\.menu-panel\s*\{(?<body>[^}]*)\}");
		Assert.True(panel.Success);
		Assert.Matches(new Regex(@"--bs-menu-transition-property:[^;]*\btranslate\b[^;]*\bdisplay\b"), panel.Groups["body"].Value);
		Assert.Matches(new Regex(@"(?<![-\w])translate:\s*0 -0\.5rem;"), panel.Groups["body"].Value);
		Assert.Matches(new Regex(@"@starting-style\s*\{\s*\.menu\.menu-panel\.show\s*\{\s*translate:"), css);
		Assert.DoesNotMatch(new Regex(@"\.menu-panel[^{]*\{[^}]*transform:\s*translate"), css);
	}

	[Fact]
	public void NavbarMenus_AllUseThePanelStyle()
	{
		// The user menu used to be a plain menu: it zoomed on desktop, only faded on phones and
		// cast a lighter shadow than the bell and queue panels beside it.
		var menusByFile = new[] { "LoginDisplay.razor", "NotificationsPanel.razor", "QueueDropdownPanel.razor" }
			.ToDictionary(file => file, file =>
			{
				var folder = file == "LoginDisplay.razor" ? new[] { "Shared" } : new[] { "Components", "Common" };
				var markup = File.ReadAllText(GetRepositoryPath(["src", "VibeSwarm.Client", .. folder, file]));
				return Regex.Matches(markup, @"<div class=""(?<classes>menu(?=[ ""])[^\n]*?)""\s*(?:style=|>)")
					.Select(match => match.Groups["classes"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					.ToList();
			});

		Assert.Equal(2, menusByFile["LoginDisplay.razor"].Count);
		foreach (var classes in menusByFile.Values.SelectMany(menus => menus))
		{
			Assert.Contains("menu-panel", classes);
			Assert.Contains("shadow-xl", classes);
			Assert.Contains("shadow-opacity-100", classes);
		}

		// The desktop gear sits at the foot of the sidebar, so its menu rises like its neighbours'.
		Assert.Single(menusByFile["LoginDisplay.razor"], classes => classes.Contains("menu-panel-up"));
	}

	[Fact]
	public void SiteCss_MovesEveryNavbarPanelOnOneMotion()
	{
		var css = ReadSiteCss();

		Assert.Contains("--vs-panel-ease: cubic-bezier(0.32, 0.72, 0, 1);", css);
		Assert.Matches(new Regex(@"--vs-panel-in:\s*0\.3s;"), css);
		Assert.Matches(new Regex(@"--vs-panel-out:\s*0\.2s;"), css);

		// Header menus come in on the shared curve and leave a little faster.
		var panel = RuleBody(css, @"\.menu\.menu-panel");
		Assert.Contains("--bs-menu-transition-timing: var(--vs-panel-ease);", panel);
		Assert.Contains("--bs-menu-transition-duration: var(--vs-panel-out);", panel);
		Assert.Contains("--bs-menu-transition-duration: var(--vs-panel-in);", RuleBody(css, @"\.menu\.menu-panel\.show"));

		// The search palette moves the same way, over the same dim.
		var search = RuleBody(css, @"\.dialog\.vs-search-dialog");
		Assert.Contains("--bs-dialog-transition-timing: var(--vs-panel-ease);", search);
		Assert.Contains("--bs-dialog-transition-duration: var(--vs-panel-in);", search);
		Assert.Contains("--bs-dialog-backdrop-bg: var(--vs-scrim-bg);", search);
		Assert.Contains("--bs-dialog-transition-duration: var(--vs-panel-out);", RuleBody(css, @"\.dialog\.vs-search-dialog\.hiding"));
		Assert.Contains("transform: translateY(-0.5rem);", RuleBody(css, @"\.dialog\.vs-search-dialog:is\(:not\(\[open\]\), \.hiding\)"));
	}

	[Fact]
	public void SiteCss_SlidesOnlyFullScreenDialogsUpFromTheBottom()
	{
		var css = ReadSiteCss();

		// On phones the search palette sits under the header; it used to fly up from below the
		// screen like the full-screen sheets.
		var sheets = Regex.Matches(css, @"(?<selector>[^{};]+)\{[^}]*translateY\(100%\)");
		Assert.NotEmpty(sheets);
		Assert.All(sheets, sheet => Assert.Contains(@".dialog.sm-down\:dialog-fullscreen", sheet.Groups["selector"].Value));
	}

	[Fact]
	public void SiteCss_OpensEveryHeaderPanelInOneFrameOnPhones()
	{
		var css = ReadSiteCss();

		// One spot and one width for every header panel, whatever width its markup gives it
		// for the desktop sidebar (inline widths used to win on phones).
		var menu = RuleBody(css, @"\.app-header \.mobile-header-dropdown > \.menu");
		Assert.Contains("top: var(--vs-panel-top) !important;", menu);
		Assert.Contains("width: var(--vs-panel-width) !important;", menu);
		Assert.Contains("max-width: none !important;", menu);
		Assert.Matches(new Regex(@"@media \(width < 1024px\)\s*\{\s*\.dialog\.vs-search-dialog\s*\{[^}]*margin-block-start:\s*var\(--vs-panel-top\);"), css);
		Assert.Matches(new Regex(@"@media \(width < 576px\)\s*\{\s*\.dialog\.vs-search-dialog\s*\{[^}]*--bs-dialog-width:\s*var\(--vs-panel-width\);"), css);
	}

	[Fact]
	public void SiteCss_DimsThePageBehindAnOpenHeaderPanel()
	{
		var css = ReadSiteCss();

		// The dim sits under the sticky header, so another header icon still answers in one tap,
		// and catches a tap on the page so it only closes the panel.
		Assert.Contains("z-index: calc(var(--bs-z-sticky) - 1);", RuleBody(css, @"\.app-layout::after"));
		// The job page's sticky bar shares the header's sticky z-index; the page's own stacking
		// layer keeps it under the dim.
		Assert.Matches(new Regex(@"@media \(width < 1024px\)\s*\{[^@]*\.app-main\s*\{[^}]*isolation:\s*isolate;"), css);
		var open = RuleBody(css, @"\.app-layout:has\(> \.app-header \.menu\.show\)::after");
		Assert.Contains("opacity: 1;", open);
		Assert.Contains("pointer-events: auto;", open);
		Assert.Matches(new Regex(@"@media \(prefers-reduced-motion: no-preference\)\s*\{\s*\.app-layout::after\s*\{\s*transition:"), css);
	}

	private static string RuleBody(string css, string selectorPattern)
	{
		var rule = Regex.Match(css, @"(?<![-\w.:>])" + selectorPattern + @"\s*\{(?<body>[^}]*)\}");
		Assert.True(rule.Success, $"No rule for {selectorPattern}");
		return rule.Groups["body"].Value;
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
