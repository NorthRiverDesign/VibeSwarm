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
	}

	[Fact]
	public async Task RenderedJobsList_ShowsPlanningProviderChain()
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

		Assert.Contains("Claude -&gt; Copilot", html);
		Assert.Contains("gpt-5.4", html);
	}

	private sealed class NoOpErrorBoundaryLogger : IErrorBoundaryLogger
	{
		public ValueTask LogErrorAsync(Exception exception)
			=> ValueTask.CompletedTask;
	}
}
