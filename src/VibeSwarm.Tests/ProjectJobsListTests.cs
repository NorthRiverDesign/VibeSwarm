using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Shared.Data;

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
				[nameof(ProjectJobsList.Jobs)] = new List<Job>
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

	private sealed class NoOpErrorBoundaryLogger : IErrorBoundaryLogger
	{
		public ValueTask LogErrorAsync(Exception exception)
			=> ValueTask.CompletedTask;
	}
}
