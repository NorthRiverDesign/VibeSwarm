using Bunit;
using Microsoft.AspNetCore.Components.Web;
using VibeSwarm.Client.Components.Dashboard;
using VibeSwarm.Client.Models;

namespace VibeSwarm.Tests;

public sealed class DashboardMetricChartTests
{
	[Fact]
	public void DashboardMetricChart_RendersFluidWidthChartLayout()
	{
		using var context = new BunitContext();

		var points = Enumerable.Range(1, 30)
			.Select(index => new DashboardChartPoint
			{
				Label = $"Day {index}",
				Value = index,
				ValueLabel = $"{index} completed jobs"
			})
			.ToList();

		var cut = context.Render<DashboardMetricChart>(parameters => parameters
			.Add(component => component.Title, "Completed Jobs")
			.Add(component => component.Subtitle, "Last 30 days")
			.Add(component => component.Points, points));

		Assert.Contains("class=\"d-block\"", cut.Markup);
		Assert.Contains("preserveAspectRatio=\"xMinYMin meet\"", cut.Markup);
		Assert.Contains("overflow-auto", cut.Markup);
		Assert.Contains("grid-template-columns:repeat(30, minmax(48px, 1fr));", cut.Markup);
		Assert.Contains("width:max(100%, 1440px);", cut.Markup);
		Assert.DoesNotContain("Tap or click a bar to view its value.", cut.Markup);
		Assert.Contains("<span class=\"text-body-secondary\">Latest</span>", cut.Markup);
		Assert.Contains("<span class=\"badge bg-body text-body\">Day 30</span>", cut.Markup);
		Assert.Contains("<span class=\"fw-semibold text-break\">30 completed jobs</span>", cut.Markup);
		Assert.DoesNotContain("top-0 end-0 translate-middle-y", cut.Markup);
		Assert.Contains("width:24px; min-width:24px; height:196px;", cut.Markup);
	}

	[Fact]
	public void DashboardMetricChart_RendersEmptyStateWhenAllPointsAreZero()
	{
		using var context = new BunitContext();

		var points = new[]
		{
			new DashboardChartPoint { Label = "Mon", Value = 0, ValueLabel = "No completed jobs" },
			new DashboardChartPoint { Label = "Tue", Value = 0, ValueLabel = "No completed jobs" }
		};

		var cut = context.Render<DashboardMetricChart>(parameters => parameters
			.Add(component => component.Title, "Completed Jobs")
			.Add(component => component.EmptyTitle, "No completed jobs yet")
			.Add(component => component.EmptyMessage, "Completed jobs from the selected time range will show up here.")
			.Add(component => component.Points, points));

		Assert.Contains("No completed jobs yet", cut.Markup);
		Assert.DoesNotContain("<svg", cut.Markup);
	}

	[Fact]
	public void DashboardMetricChart_SelectingBarShowsPointValue()
	{
		using var context = new BunitContext();

		var points = new[]
		{
			new DashboardChartPoint { Label = "Day 1", Value = 3, ValueLabel = "3 completed jobs" },
			new DashboardChartPoint { Label = "Day 2", Value = 6, ValueLabel = "6 completed jobs" },
			new DashboardChartPoint { Label = "Day 3", Value = 9, ValueLabel = "9 completed jobs" }
		};

		var cut = context.Render<DashboardMetricChart>(parameters => parameters
			.Add(component => component.Title, "Completed Jobs")
			.Add(component => component.Points, points)
			.Add(component => component.UseIntegerYAxisTicks, true)
			.Add(component => component.YAxisLabelFormatter, value => value.ToString("0")));

		Assert.DoesNotContain("Tap or click a bar to view its value.", cut.Markup);
		Assert.Contains("<span class=\"text-body-secondary\">Latest</span>", cut.Markup);
		Assert.Contains("<span class=\"badge bg-body text-body\">Day 3</span>", cut.Markup);
		Assert.Contains("<span class=\"fw-semibold text-break\">9 completed jobs</span>", cut.Markup);
		Assert.Contains(">9</div>", cut.Markup);
		Assert.Contains(">6</div>", cut.Markup);
		Assert.Contains(">3</div>", cut.Markup);
		Assert.Contains(">0</div>", cut.Markup);
		Assert.Contains("Day 1: 3 completed jobs", cut.Markup);

		var buttons = cut.FindAll("button[aria-label]");
		Assert.Equal(3, buttons.Count);

		buttons[1].TriggerEvent("onclick", new MouseEventArgs());

		cut.WaitForAssertion(() =>
		{
			Assert.DoesNotContain("Tap or click a bar to view its value.", cut.Markup);
			Assert.Contains("<span class=\"badge bg-body text-body\">Day 2</span>", cut.Markup);
			Assert.Contains("<span class=\"fw-semibold text-break\">6 completed jobs</span>", cut.Markup);
			Assert.Contains("<span class=\"text-body-secondary\">Selected</span>", cut.Markup);
			Assert.Contains("aria-pressed=\"true\"", cut.Markup);
		});
	}

	[Fact]
	public void DashboardMetricChart_UsesTighterNiceScaleForFractionalYAxisValues()
	{
		using var context = new BunitContext();

		var points = new[]
		{
			new DashboardChartPoint { Label = "Day 1", Value = 61, ValueLabel = "61 minutes" },
			new DashboardChartPoint { Label = "Day 2", Value = 47, ValueLabel = "47 minutes" },
			new DashboardChartPoint { Label = "Day 3", Value = 22, ValueLabel = "22 minutes" }
		};

		var cut = context.Render<DashboardMetricChart>(parameters => parameters
			.Add(component => component.Title, "Average Job Duration")
			.Add(component => component.Points, points)
			.Add(component => component.YAxisLabelFormatter, value => value.ToString("0")));

		Assert.Contains(">75</div>", cut.Markup);
		Assert.Contains(">50</div>", cut.Markup);
		Assert.Contains(">25</div>", cut.Markup);
		Assert.Contains(">0</div>", cut.Markup);
		Assert.DoesNotContain(">150</div>", cut.Markup);
		Assert.Contains("width:24px; min-width:24px; height:196px;", cut.Markup);
		Assert.DoesNotContain("width:56px;", cut.Markup);
	}
}
