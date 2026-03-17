using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Dashboard;
using VibeSwarm.Client.Models;

namespace VibeSwarm.Tests;

public sealed class DashboardMetricChartTests
{
	[Fact]
	public async Task DashboardMetricChart_RendersFluidWidthChartLayout()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var points = Enumerable.Range(1, 30)
			.Select(index => new DashboardChartPoint
			{
				Label = $"Day {index}",
				Value = index,
				ValueLabel = $"{index} completed jobs"
			})
			.ToList();

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(DashboardMetricChart.Title)] = "Completed Jobs",
				[nameof(DashboardMetricChart.Subtitle)] = "Last 30 days",
				[nameof(DashboardMetricChart.Points)] = points
			});

			var output = await renderer.RenderComponentAsync<DashboardMetricChart>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("class=\"d-block w-100\"", html);
		Assert.Contains("preserveAspectRatio=\"none\"", html);
		Assert.Contains("grid-template-columns: repeat(30, minmax(0, 1fr));", html);
		Assert.DoesNotContain("overflow-auto", html);
		Assert.DoesNotContain("Scroll to view the full time range.", html);
	}

	[Fact]
	public async Task DashboardMetricChart_RendersEmptyStateWhenAllPointsAreZero()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var points = new[]
		{
			new DashboardChartPoint { Label = "Mon", Value = 0, ValueLabel = "No completed jobs" },
			new DashboardChartPoint { Label = "Tue", Value = 0, ValueLabel = "No completed jobs" }
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(DashboardMetricChart.Title)] = "Completed Jobs",
				[nameof(DashboardMetricChart.EmptyTitle)] = "No completed jobs yet",
				[nameof(DashboardMetricChart.EmptyMessage)] = "Completed jobs from the selected time range will show up here.",
				[nameof(DashboardMetricChart.Points)] = points
			});

			var output = await renderer.RenderComponentAsync<DashboardMetricChart>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("No completed jobs yet", html);
		Assert.DoesNotContain("<svg", html);
	}

	[Fact]
	public async Task DashboardMetricChart_RendersYAxisLabels_AndExpandedTooltipTargets()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var points = new[]
		{
			new DashboardChartPoint { Label = "Day 1", Value = 3, ValueLabel = "3 completed jobs" },
			new DashboardChartPoint { Label = "Day 2", Value = 6, ValueLabel = "6 completed jobs" },
			new DashboardChartPoint { Label = "Day 3", Value = 9, ValueLabel = "9 completed jobs" }
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(DashboardMetricChart.Title)] = "Completed Jobs",
				[nameof(DashboardMetricChart.Points)] = points,
				[nameof(DashboardMetricChart.UseIntegerYAxisTicks)] = true,
				[nameof(DashboardMetricChart.YAxisLabelFormatter)] = (Func<double, string>)(value => value.ToString("0"))
			});

			var output = await renderer.RenderComponentAsync<DashboardMetricChart>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("fill=\"transparent\"", html);
		Assert.Contains("text-anchor=\"end\"", html);
		Assert.Contains(">9</text>", html);
		Assert.Contains(">6</text>", html);
		Assert.Contains(">3</text>", html);
		Assert.Contains(">0</text>", html);
		Assert.Contains("font-size:0.72rem", html);
		Assert.Contains("title=\"Day 1\"", html);
		Assert.Contains("Day 1: 3 completed jobs", html);
	}
}
