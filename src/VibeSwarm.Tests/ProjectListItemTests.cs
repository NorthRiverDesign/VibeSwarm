using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectListItemTests
{
	[Fact]
	public async Task RenderedProjectListItem_ShowsLatestJobSummaryWithoutOutcomeGuidance()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IErrorBoundaryLogger, NoOpErrorBoundaryLogger>();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ProjectListItem.Project)] = new Project
				{
					Id = Guid.NewGuid(),
					Name = "Outcome project",
					IsActive = true
				},
				[nameof(ProjectListItem.Stats)] = new ProjectJobStats(),
				[nameof(ProjectListItem.LatestJob)] = new JobSummary
				{
					Id = Guid.NewGuid(),
					GoalPrompt = "Ship the release",
					Title = "Ship the release",
					Status = JobStatus.Completed,
					PullRequestNumber = 42,
					PullRequestUrl = "https://github.com/octo-org/octo-repo/pull/42",
					SessionSummary = "Polished the delivery summary for the latest project run.\nHidden detail.",
					CreatedAt = DateTime.UtcNow
				}
			});

			var output = await renderer.RenderComponentAsync<ProjectListItem>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Work summary:", html);
		Assert.Contains("Polished the delivery summary for the latest project run.", html);
		Assert.DoesNotContain("Hidden detail.", html);
		Assert.DoesNotContain("PR #42 ready.", html);
		Assert.DoesNotContain("Review it and merge when the changes are approved.", html);
	}

	private sealed class NoOpErrorBoundaryLogger : IErrorBoundaryLogger
	{
		public ValueTask LogErrorAsync(Exception exception)
			=> ValueTask.CompletedTask;
	}
}
