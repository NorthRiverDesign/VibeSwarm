using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class JobSessionPanelTests
{
	[Fact]
	public async Task RenderedJobSessionPanel_UsesLiveTranscriptWhenPersistedMessagesAreSparse()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(JobSessionPanel.Status)] = JobStatus.Completed,
				[nameof(JobSessionPanel.Messages)] = new List<JobMessage>
				{
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.Assistant,
						Content = "Final summary only",
						CreatedAt = DateTime.UtcNow.AddSeconds(-5)
					}
				},
				[nameof(JobSessionPanel.LiveOutputLines)] = new List<OutputLine>
				{
					new()
					{
						Content = "Investigating the repository state",
						Timestamp = DateTime.UtcNow.AddSeconds(-10)
					},
					new()
					{
						Content = "[Tool] bash: git status --short",
						ContentCategory = "tool",
						Timestamp = DateTime.UtcNow.AddSeconds(-9)
					},
					new()
					{
						Content = "[Tool Result] bash: M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
						ContentCategory = "tool",
						Timestamp = DateTime.UtcNow.AddSeconds(-8)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("3 messages", html);
		Assert.Contains("Tool Call", html);
		Assert.Contains("Tool Result", html);
		Assert.Contains("git status --short", html);
		Assert.DoesNotContain("Final summary only", html);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
