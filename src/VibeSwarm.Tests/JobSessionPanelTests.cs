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

	[Fact]
	public async Task RenderedJobSessionPanel_ShowsCliWaitStateAsInlineStatus()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(JobSessionPanel.Status)] = JobStatus.Processing,
				[nameof(JobSessionPanel.IsJobActive)] = true,
				[nameof(JobSessionPanel.CurrentActivity)] = "Waiting for CLI response (12s)...",
				[nameof(JobSessionPanel.LiveOutputLines)] = new List<OutputLine>
				{
					new()
					{
						Content = "[Assistant] First response chunk",
						Timestamp = DateTime.UtcNow.AddSeconds(-10)
					},
					new()
					{
						Content = "[VibeSwarm] Still waiting for response... (waited 12s).",
						Timestamp = DateTime.UtcNow.AddSeconds(-5)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("1 messages", html);
		Assert.Contains("First response chunk", html);
		Assert.Contains("Waiting for CLI response (12s)...", html);
		Assert.DoesNotContain("[VibeSwarm] Still waiting for response", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_KeepsSystemMessagesSeparateAndStyled()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(JobSessionPanel.Status)] = JobStatus.Processing,
				[nameof(JobSessionPanel.IsJobActive)] = true,
				[nameof(JobSessionPanel.LiveOutputLines)] = new List<OutputLine>
				{
					new()
					{
						Content = "[VibeSwarm] Process started (PID: 123). Waiting for CLI to initialize...",
						Timestamp = DateTime.UtcNow.AddSeconds(-12)
					},
					new()
					{
						Content = "[Connection] Connected to provider stream",
						Timestamp = DateTime.UtcNow.AddSeconds(-11)
					},
					new()
					{
						Content = "[Assistant] Gathering repository context",
						Timestamp = DateTime.UtcNow.AddSeconds(-10)
					},
					new()
					{
						Content = "[Retry] Transient error (attempt 1/3): provider timeout",
						Timestamp = DateTime.UtcNow.AddSeconds(-9)
					},
					new()
					{
						Content = "[Session] Complete",
						Timestamp = DateTime.UtcNow.AddSeconds(-8)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("5 messages", html);
		Assert.Contains("VibeSwarm", html);
		Assert.Contains("Provider", html);
		Assert.Contains("bg-primary-subtle", html);
		Assert.Contains("bg-success-subtle", html);
		Assert.Contains("bg-warning-subtle", html);
		Assert.Contains("Process started (PID: 123)", html);
		Assert.Contains("[Connection] Connected to provider stream", html);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
