using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Common.Primitives;

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
	public void SiteCss_ClearsTheHomeIndicatorOnlyAtTheBottomEdgeOfContent()
	{
		var css = ReadSiteCss();

		Assert.Contains("--vs-safe-area-bottom: env(safe-area-inset-bottom, 0px);", css);
		Assert.Matches(new Regex(@"\.main-content\s*\{[^}]*padding-bottom:\s*max\(1rem, var\(--vs-safe-area-bottom\)\);"), css);
		// A full-screen modal pads whichever band is last, never the body and the footer both.
		Assert.Matches(new Regex(@"\.vs-modal-dialog \.modal-body:last-child\s*\{[^}]*max\("), css);
		Assert.Matches(new Regex(@"\.vs-modal-dialog \.modal-footer\s*\{[^}]*padding-bottom:\s*max\(0\.75rem, var\(--vs-safe-area-bottom\)\);"), css);
	}

	[Theory]
	[InlineData("sm", "status-disc-sm")]
	[InlineData("md", "status-disc-md")]
	[InlineData("lg", "status-disc-lg")]
	public async Task StatusIconPill_RendersAFixedSizeCircle(string size, string expectedSizeClass)
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(StatusIconPill.Status)] = "completed",
				[nameof(StatusIconPill.Size)] = size
			});

			var output = await renderer.RenderComponentAsync<StatusIconPill>(parameters);
			return output.ToHtmlString();
		});

		// A padded badge takes its height from the icon font's line box and renders as an oval.
		Assert.Contains("rounded-circle", html);
		Assert.Contains(expectedSizeClass, html);
		Assert.DoesNotContain("badge", html);
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
