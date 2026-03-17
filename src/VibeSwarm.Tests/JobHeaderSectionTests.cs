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
}
