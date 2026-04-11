using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectJobsListTests
{
	[Fact]
	public async Task RenderedJobsList_ShowsMoreMenuWithBulkActions_WithoutNestedCardHeader()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IErrorBoundaryLogger, NoOpErrorBoundaryLogger>();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ProjectJobsList.Jobs)] = new List<JobSummary>
				{
					new()
					{
						Id = Guid.NewGuid(),
						GoalPrompt = "Completed job",
						Title = "Completed job",
						Status = JobStatus.Completed,
						CreatedAt = DateTime.UtcNow
					}
				},
				[nameof(ProjectJobsList.TotalJobsCount)] = 1,
				[nameof(ProjectJobsList.HasActiveJobs)] = true,
				[nameof(ProjectJobsList.HasCompletedJobs)] = true
			});
			var output = await renderer.RenderComponentAsync<ProjectJobsList>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("More job actions", html);
		Assert.Contains("Cancel All", html);
		Assert.Contains("Delete Completed Jobs", html);
		Assert.Contains("Queue active", html);
		Assert.DoesNotContain("card-header", html);
		Assert.Contains("border rounded-3", html);
		Assert.DoesNotContain("border rounded-3 overflow-hidden", html);
		Assert.Contains("d-flex flex-column flex-lg-row align-items-stretch align-items-lg-center gap-2", html);
		Assert.Contains("row g-2 align-items-stretch", html);
		Assert.Contains("col-12 col-md-8", html);
		Assert.Contains("col-12 col-md-4", html);
		Assert.Contains("d-flex flex-column flex-sm-row justify-content-between align-items-stretch align-items-sm-center gap-2", html);
		Assert.Contains("d-grid d-sm-flex align-items-stretch align-items-sm-center gap-2", html);
	}

	[Fact]
	public async Task RenderedJobsList_HidesProviderChainByDefault()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IErrorBoundaryLogger, NoOpErrorBoundaryLogger>();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ProjectJobsList.Jobs)] = new List<JobSummary>
				{
					new()
					{
						Id = Guid.NewGuid(),
						GoalPrompt = "Mixed provider job",
						Title = "Mixed provider job",
						Status = JobStatus.Completed,
						ProviderName = "Copilot",
						ModelUsed = "gpt-5.4",
						PlanningProviderName = "Claude",
						PlanningModelUsed = "claude-sonnet-4",
						CreatedAt = DateTime.UtcNow
					}
				},
				[nameof(ProjectJobsList.TotalJobsCount)] = 1
			});
			var output = await renderer.RenderComponentAsync<ProjectJobsList>(parameters);
			return output.ToHtmlString();
		});

		Assert.DoesNotContain("Claude -&gt; Copilot", html);
		Assert.DoesNotContain("gpt-5.4", html);
	}

	[Fact]
	public async Task RenderedJobsList_ShowsCompactWorkSummary()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IErrorBoundaryLogger, NoOpErrorBoundaryLogger>();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ProjectJobsList.Jobs)] = new List<JobSummary>
				{
					new()
					{
						Id = Guid.NewGuid(),
						GoalPrompt = "Summarize the run",
						Title = "Summarize the run",
						Status = JobStatus.Completed,
						SessionSummary = "Improved the delivery guidance on the project jobs list.\nAdded a second line that should stay hidden.",
						CreatedAt = DateTime.UtcNow
					}
				},
				[nameof(ProjectJobsList.TotalJobsCount)] = 1
			});
			var output = await renderer.RenderComponentAsync<ProjectJobsList>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Work summary:", html);
		Assert.Contains("Improved the delivery guidance on the project jobs list.", html);
		Assert.DoesNotContain("Added a second line that should stay hidden.", html);
	}

	private sealed class NoOpErrorBoundaryLogger : IErrorBoundaryLogger
	{
		public ValueTask LogErrorAsync(Exception exception)
			=> ValueTask.CompletedTask;
	}

	[Fact]
	public void ProjectJobsList_BulkSelection_InvokesEligibleCallbacks()
	{
		using var context = new BunitContext();
		List<Guid>? retriedIds = null;
		List<Guid>? cancelledIds = null;
		List<Guid>? prioritizedIds = null;

		var failedJobId = Guid.NewGuid();
		var stalledJobId = Guid.NewGuid();
		var queuedJobId = Guid.NewGuid();
		var completedJobId = Guid.NewGuid();

		var cut = context.Render<ProjectJobsList>(parameters => parameters
			.Add(component => component.Jobs, new List<JobSummary>
			{
				new()
				{
					Id = failedJobId,
					GoalPrompt = "Retry me",
					Title = "Retry me",
					Status = JobStatus.Failed,
					CreatedAt = DateTime.UtcNow
				},
				new()
				{
					Id = stalledJobId,
					GoalPrompt = "Stopped job",
					Title = "Stopped job",
					Status = JobStatus.Stalled,
					CreatedAt = DateTime.UtcNow
				},
				new()
				{
					Id = queuedJobId,
					GoalPrompt = "Queue me",
					Title = "Queue me",
					Status = JobStatus.New,
					CreatedAt = DateTime.UtcNow
				},
				new()
				{
					Id = completedJobId,
					GoalPrompt = "Done",
					Title = "Done",
					Status = JobStatus.Completed,
					CreatedAt = DateTime.UtcNow
				}
			})
			.Add(component => component.TotalJobsCount, 4)
			.Add(component => component.OnRetrySelected, EventCallback.Factory.Create<List<Guid>>(this, ids => retriedIds = ids))
			.Add(component => component.OnCancelSelected, EventCallback.Factory.Create<List<Guid>>(this, ids => cancelledIds = ids))
			.Add(component => component.OnPrioritizeSelected, EventCallback.Factory.Create<List<Guid>>(this, ids => prioritizedIds = ids)));

		cut.Find("input[aria-label='Select job Retry me']").Change(true);
		cut.Find("input[aria-label='Select job Stopped job']").Change(true);
		cut.Find("input[aria-label='Select job Queue me']").Change(true);
		cut.Find("input[aria-label='Select job Done']").Change(true);

		Assert.Contains("4 selected", cut.Markup);
		Assert.Contains("Retry Selected (2)", cut.Markup);
		Assert.Contains("Cancel Selected (2)", cut.Markup);
		Assert.Contains("Prioritize Selected (1)", cut.Markup);

		cut.Find("button[title='Retry selected failed, cancelled, or stopped jobs']").Click();
		Assert.Equal([failedJobId, stalledJobId], retriedIds);

		cut.Find("input[aria-label='Select job Retry me']").Change(true);
		cut.Find("input[aria-label='Select job Stopped job']").Change(true);
		cut.Find("input[aria-label='Select job Queue me']").Change(true);
		cut.Find("button[title='Cancel selected queued or active jobs']").Click();
		Assert.Equal([stalledJobId, queuedJobId], cancelledIds);

		cut.Find("input[aria-label='Select job Retry me']").Change(true);
		cut.Find("input[aria-label='Select job Stopped job']").Change(true);
		cut.Find("input[aria-label='Select job Queue me']").Change(true);
		cut.Find("button[title='Move selected queued jobs to the front']").Click();
		Assert.Equal([queuedJobId], prioritizedIds);
	}
}
