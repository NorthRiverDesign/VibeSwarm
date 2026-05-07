using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Common;

namespace VibeSwarm.Tests;

public sealed class PaginationControlsTests
{
	[Fact]
	public async Task RenderedPaginationControls_HidesFooter_WhenResultsFitOnSinglePage()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(PaginationControls.PageNumber)] = 1,
				[nameof(PaginationControls.PageSize)] = 10,
				[nameof(PaginationControls.TotalCount)] = 10
			});

			var output = await renderer.RenderComponentAsync<PaginationControls>(parameters);
			return output.ToHtmlString();
		});

		Assert.DoesNotContain("Showing 1-10 of 10", html);
		Assert.DoesNotContain("Page 1 of 1", html);
		Assert.DoesNotContain("aria-label=\"Pagination\"", html);
	}

	[Fact]
	public async Task RenderedPaginationControls_RendersFooter_WhenResultsSpanMultiplePages()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(PaginationControls.PageNumber)] = 1,
				[nameof(PaginationControls.PageSize)] = 10,
				[nameof(PaginationControls.TotalCount)] = 11
			});

			var output = await renderer.RenderComponentAsync<PaginationControls>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Showing 1-10 of 11", html);
		Assert.Contains("Page 1 of 2", html);
		Assert.Contains("aria-label=\"Pagination\"", html);
	}
}
