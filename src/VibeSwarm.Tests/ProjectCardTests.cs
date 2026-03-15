using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class ProjectCardTests
{
	[Fact]
	public async Task RenderedProjectCard_UsesIdeaTitleForLatestJobPreview()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var latestJob = new Job
		{
			Id = Guid.NewGuid(),
			Title = "Use the idea text for previews",
			GoalPrompt = "You are implementing a feature based on the following idea. Long expanded prompt...",
			Status = JobStatus.Processing,
			CreatedAt = DateTime.UtcNow
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ProjectCard.ProjectId)] = Guid.NewGuid(),
				[nameof(ProjectCard.Name)] = "Preview Project",
				[nameof(ProjectCard.WorkingPath)] = "/tmp/preview-project",
				[nameof(ProjectCard.LatestJob)] = latestJob,
				[nameof(ProjectCard.CreatedAt)] = DateTime.UtcNow
			});

			var output = await renderer.RenderComponentAsync<ProjectCard>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Use the idea text for previews", html);
		Assert.DoesNotContain("You are implementing a feature based on the following idea", html);
	}
}
