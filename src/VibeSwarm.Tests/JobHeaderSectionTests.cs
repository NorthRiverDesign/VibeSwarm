using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class JobHeaderSectionTests
{
	[Fact]
	public async Task RenderedJobHeaderSection_PairsProviderAndModelWithoutActivityAlert()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(JobHeaderSection.Status)] = JobStatus.Processing,
				[nameof(JobHeaderSection.JobTitle)] = "Polish the UI",
				[nameof(JobHeaderSection.ProviderName)] = "Copilot",
				[nameof(JobHeaderSection.ModelUsed)] = "gpt-5.4",
				[nameof(JobHeaderSection.BranchName)] = "feature/ui-cleanup",
				[nameof(JobHeaderSection.CreatedAt)] = DateTime.UtcNow.AddMinutes(-10),
				[nameof(JobHeaderSection.StartedAt)] = DateTime.UtcNow.AddMinutes(-8),
				[nameof(JobHeaderSection.CurrentActivity)] = "Compiling changes",
				[nameof(JobHeaderSection.LastActivityAt)] = DateTime.UtcNow.AddMinutes(-1)
			});

			var output = await renderer.RenderComponentAsync<JobHeaderSection>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Copilot / gpt-5.4", html);
		Assert.Contains("feature/ui-cleanup", html);
		Assert.DoesNotContain("Compiling changes", html);
	}

	[Fact]
	public async Task RenderedJobHeaderSection_ShowsPlanningAndExecutionProvidersWithStageBreakdown()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(JobHeaderSection.Status)] = JobStatus.Completed,
				[nameof(JobHeaderSection.JobTitle)] = "Implement mixed-provider flow",
				[nameof(JobHeaderSection.ProviderName)] = "Copilot",
				[nameof(JobHeaderSection.ModelUsed)] = "gpt-5.4",
				[nameof(JobHeaderSection.PlanningProviderName)] = "Claude",
				[nameof(JobHeaderSection.PlanningModelUsed)] = "claude-sonnet-4",
				[nameof(JobHeaderSection.InputTokens)] = 450,
				[nameof(JobHeaderSection.OutputTokens)] = 300,
				[nameof(JobHeaderSection.TotalCostUsd)] = 2.10m,
				[nameof(JobHeaderSection.PlanningInputTokens)] = 150,
				[nameof(JobHeaderSection.PlanningOutputTokens)] = 50,
				[nameof(JobHeaderSection.PlanningCostUsd)] = 0.60m,
				[nameof(JobHeaderSection.ExecutionInputTokens)] = 300,
				[nameof(JobHeaderSection.ExecutionOutputTokens)] = 250,
				[nameof(JobHeaderSection.ExecutionCostUsd)] = 1.50m,
				[nameof(JobHeaderSection.CreatedAt)] = DateTime.UtcNow.AddMinutes(-10),
				[nameof(JobHeaderSection.CompletedAt)] = DateTime.UtcNow.AddMinutes(-1)
			});

			var output = await renderer.RenderComponentAsync<JobHeaderSection>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Claude / claude-sonnet-4 -&gt; Copilot / gpt-5.4", html);
		Assert.Contains("Planning</dt>", html);
		Assert.Contains("200 / $0.60", html);
		Assert.Contains("Execution</dt>", html);
		Assert.Contains("550 / $1.50", html);
		Assert.Contains("$0.60", html);
		Assert.Contains("$1.50", html);
	}
	[Fact]
	public void JobHeaderSection_Bunit_GroupsEveryWayToStopARunningJobInOneMenu()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobHeaderSection>(parameters => parameters
			.Add(header => header.Status, JobStatus.Processing)
			.Add(header => header.JobTitle, "Polish the UI")
			.Add(header => header.CanCancel, true)
			.Add(header => header.CanForceReset, true)
			.Add(header => header.CanRetry, true)
			.Add(header => header.CreatedAt, DateTime.UtcNow));

		var stopMenu = cut.Find("button[aria-label='Stop'] + .menu");
		var stopItems = stopMenu.QuerySelectorAll(".menu-item").Select(item => item.TextContent.Trim()).ToList();
		Assert.Equal(["Stop job", "Mark as failed"], stopItems);

		// Retry lives behind the ⋯ menu, not beside the stop actions.
		var moreMenu = cut.Find("button[aria-label='Job actions'] + .menu");
		Assert.Contains("Retry", moreMenu.TextContent);
		Assert.DoesNotContain("Mark as failed", moreMenu.TextContent);
	}

	[Fact]
	public void JobHeaderSection_Bunit_OffersForceStopOnceAStopIsRequested()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobHeaderSection>(parameters => parameters
			.Add(header => header.Status, JobStatus.Processing)
			.Add(header => header.CancellationRequested, true)
			.Add(header => header.CanForceCancel, true)
			.Add(header => header.CanForceReset, true)
			.Add(header => header.CreatedAt, DateTime.UtcNow));

		var stopItems = cut.Find("button[aria-label='Stop'] + .menu")
			.QuerySelectorAll(".menu-item").Select(item => item.TextContent.Trim()).ToList();
		Assert.Equal(["Force stop", "Mark as failed"], stopItems);
		Assert.Contains("Stopping…", cut.Markup);
	}

	[Fact]
	public void JobHeaderSection_Bunit_ClampsALongTitleAndLinksBack()
	{
		using var context = new BunitContext();
		var longTitle = string.Concat(Enumerable.Repeat("Make the running job page feel native on a phone. ", 4));

		var cut = context.Render<JobHeaderSection>(parameters => parameters
			.Add(header => header.Status, JobStatus.Completed)
			.Add(header => header.JobTitle, longTitle)
			.Add(header => header.BackHref, "/jobs")
			.Add(header => header.BackLabel, "Jobs")
			.Add(header => header.CreatedAt, DateTime.UtcNow));

		Assert.Contains("line-clamp-2", cut.Find("h1").ClassName);
		Assert.Empty(cut.FindAll("button[aria-label='Stop']"));
		Assert.Equal("/jobs", cut.Find(".job-nav-bar a").GetAttribute("href"));
		Assert.Contains("Jobs", cut.Find(".job-nav-bar a").TextContent);

		cut.Find("h1").Click();

		Assert.DoesNotContain("line-clamp-2", cut.Find("h1").ClassName ?? string.Empty);
	}
}
