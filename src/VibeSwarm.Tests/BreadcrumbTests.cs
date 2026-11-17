using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Common;

namespace VibeSwarm.Tests;

public sealed class BreadcrumbTests
{
	[Fact]
	public async Task Breadcrumb_OnPhonesShowsOneBackLinkToThePageAbove()
	{
		var html = await RenderAsync(
		[
			new("Projects", "/projects", "folder"),
			new("Demo Project", "/projects/42"),
			new("Fix the build"),
		]);

		// The whole trail wraps onto extra lines on a phone and repeats the page title.
		Assert.Matches(new Regex(@"<div class=""md:d-none[^""]*"">\s*<a href=""/projects/42""[^>]*>\s*<i class=""bi bi-chevron-left""></i>Demo Project\s*</a>"), html);
		Assert.Matches(new Regex(@"<nav aria-label=""breadcrumb"" class=""mb-5 d-none md:d-block"">"), html);
		Assert.Contains("Fix the build", html);
	}

	[Fact]
	public async Task Breadcrumb_WithNoPageAbove_ShowsTheTrailOnEveryScreen()
	{
		var html = await RenderAsync([new("Settings")]);

		Assert.DoesNotContain("bi-chevron-left", html);
		Assert.DoesNotContain("d-none", html);
		Assert.Contains("Settings", html);
	}

	private static async Task<string> RenderAsync(List<Breadcrumb.BreadcrumbItem> items)
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(Breadcrumb.Items)] = items
			});

			var output = await renderer.RenderComponentAsync<Breadcrumb>(parameters);
			return output.ToHtmlString();
		});
	}
}
