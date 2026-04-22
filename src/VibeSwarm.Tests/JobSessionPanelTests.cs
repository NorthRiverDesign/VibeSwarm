using Bunit;
using System.Text.RegularExpressions;
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

		Assert.Contains("2 messages", html);
		Assert.Contains("Tool Activity", html);
		Assert.Contains("git status --short", html);
		Assert.Contains("Response", html);
		Assert.Contains("<details>", html);
		Assert.DoesNotContain("<details open", html);
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
						Content = "[System] Still waiting for response... (waited 12s).",
						Timestamp = DateTime.UtcNow.AddSeconds(-5)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("1 message", html);
		Assert.Contains("First response chunk", html);
		Assert.Contains("Waiting for CLI response (12s)...", html);
		Assert.DoesNotContain("[System] Still waiting for response", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_MergesLiveUserMessagesIntoActiveTranscript()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var providerTimestamp = DateTime.UtcNow.AddSeconds(-10);
		var userTimestamp = providerTimestamp.AddSeconds(5);

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
						Content = "[Assistant] Please confirm which failing test to rerun.",
						Timestamp = providerTimestamp
					}
				},
				[nameof(JobSessionPanel.LiveMessages)] = new List<JobMessage>
				{
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.User,
						Content = "Rerun the failing provider tests first.",
						CreatedAt = userTimestamp
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("2 messages", html);
		Assert.Contains("Please confirm which failing test to rerun.", html);
		Assert.Contains("Rerun the failing provider tests first.", html);
		Assert.Contains("chat-message-user", html);
		Assert.True(
			html.IndexOf("Please confirm which failing test to rerun.", StringComparison.Ordinal)
				< html.IndexOf("Rerun the failing provider tests first.", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RenderedJobSessionPanel_HidesCommitSummaryBlocksFromDisplayedMessages()
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
						Content = """
							## Plan
							1. Capture the planning summary.
							2. Prefer it for git delivery.

							<commit-summary>
							hidden planning git summary
							</commit-summary>
							""",
						CreatedAt = DateTime.UtcNow.AddSeconds(-5)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Capture the planning summary", html);
		Assert.Contains("Prefer it for git delivery", html);
		Assert.DoesNotContain("hidden planning git summary", html);
		Assert.DoesNotContain("&lt;commit-summary&gt;", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_ShowsPersistedUserMessagesAsUserEntries()
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
						Content = "Initial implementation is complete.",
						CreatedAt = DateTime.UtcNow.AddSeconds(-10)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.User,
						Content = "Please also cover the remaining edge case.",
						CreatedAt = DateTime.UtcNow.AddSeconds(-5)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("2 messages", html);
		Assert.Contains("Initial implementation is complete.", html);
		Assert.Contains("Please also cover the remaining edge case.", html);
		Assert.Contains("chat-message-user", html);
		Assert.True(
			html.IndexOf("Initial implementation is complete.", StringComparison.Ordinal)
				< html.IndexOf("Please also cover the remaining edge case.", StringComparison.Ordinal));
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
						Content = "[System] Process started (PID: 123). Waiting for CLI to initialize...",
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
		Assert.Contains("System", html);
		Assert.Contains("Provider", html);
		Assert.Contains("bg-primary-subtle", html);
		Assert.Contains("bg-success-subtle", html);
		Assert.Contains("bg-warning-subtle", html);
		Assert.Contains("Process started (PID: 123)", html);
		Assert.Contains("Connected to provider stream", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_DeduplicatesAdjacentProcessStartedMessages()
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
						Content = "[System] Process started (PID: 456). Waiting for CLI to initialize...",
						Timestamp = DateTime.UtcNow.AddSeconds(-12)
					},
					new()
					{
						Content = "[System] Process started (PID: 456). Waiting for CLI to initialize...",
						Timestamp = DateTime.UtcNow.AddSeconds(-11)
					},
					new()
					{
						Content = "[Assistant] Gathering repository context",
						Timestamp = DateTime.UtcNow.AddSeconds(-10)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("2 messages", html);
		Assert.Single(Regex.Matches(html, Regex.Escape("Process started (PID: 456). Waiting for CLI to initialize...")).Cast<Match>());
	}

	[Fact]
	public async Task RenderedJobSessionPanel_DeduplicatesMergedProcessStartedMessages()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var timestamp = DateTime.UtcNow;
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
						Content = "[System] Process started (PID: 789). Waiting for CLI to initialize...",
						Timestamp = timestamp.AddSeconds(-12)
					}
				},
				[nameof(JobSessionPanel.LiveMessages)] = new List<JobMessage>
				{
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.System,
						Content = "[System] Process started (PID: 789). Waiting for CLI to initialize...",
						CreatedAt = timestamp.AddSeconds(-11)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.Assistant,
						Content = "Retrying after startup failure.",
						CreatedAt = timestamp.AddSeconds(-10)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Single(Regex.Matches(html, Regex.Escape("Process started (PID: 789). Waiting for CLI to initialize...")).Cast<Match>());
		Assert.Contains("Retrying after startup failure.", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_DeduplicatesDifferentPidProcessStartedMessages()
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
						Content = "[System] Process started (PID: 100). Waiting for CLI to initialize...",
						Timestamp = DateTime.UtcNow.AddSeconds(-12)
					},
					new()
					{
						Content = "[Assistant] Gathering repository context",
						Timestamp = DateTime.UtcNow.AddSeconds(-11)
					},
					new()
					{
						Content = "[System] Process started (PID: 200). Waiting for CLI to initialize...",
						Timestamp = DateTime.UtcNow.AddSeconds(-10)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Single(Regex.Matches(html, "Process started \\(PID: \\d+\\)").Cast<Match>());
	}

	[Fact]
	public async Task RenderedJobSessionPanel_AllowsSecondProcessStartedAfterRetry()
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
						Content = "[System] Process started (PID: 100). Waiting for CLI to initialize...",
						Timestamp = DateTime.UtcNow.AddSeconds(-12)
					},
					new()
					{
						Content = "[Retry] Restarting after failure",
						Timestamp = DateTime.UtcNow.AddSeconds(-11)
					},
					new()
					{
						Content = "[System] Process started (PID: 200). Waiting for CLI to initialize...",
						Timestamp = DateTime.UtcNow.AddSeconds(-10)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Equal(2, Regex.Matches(html, "Process started \\(PID: \\d+\\)").Count);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_NormalizesPersistedClaudeMessagesAsProviderEntries()
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
						Role = MessageRole.System,
						Content = "Installing dependencies with npm",
						CreatedAt = DateTime.UtcNow.AddSeconds(-5)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.System,
						Content = "[System] Process started (PID: 123). Waiting for CLI to initialize...",
						CreatedAt = DateTime.UtcNow.AddSeconds(-4)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Installing dependencies with npm", html);
		Assert.Contains("Process started (PID: 123)", html);
		Assert.Contains("Provider", html);
		Assert.Contains("System", html);
		Assert.Contains("chat-message-assistant", html);
		Assert.Contains("chat-message-system", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_ReplacesGeneratedToolResultIdsWithToolNames()
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
						Role = MessageRole.ToolUse,
						Content = "bash",
						ToolName = "bash",
						ToolInput = "git status --short",
						CreatedAt = DateTime.UtcNow.AddSeconds(-2)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
						ToolName = "toolu_01A2B3C4",
						ToolOutput = "M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
						CreatedAt = DateTime.UtcNow.AddSeconds(-1)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("1 message", html);
		Assert.Contains("bash", html);
		Assert.Contains("Tool Activity", html);
		Assert.Contains("git status --short", html);
		Assert.Contains("M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor", html);
		Assert.DoesNotContain("toolu_01A2B3C4", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_PairsInterleavedToolMessagesWithEarliestCompatibleUse()
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
						Role = MessageRole.ToolUse,
						Content = "pending",
						ToolInput = "placeholder command",
						CreatedAt = DateTime.UtcNow.AddSeconds(-4)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolUse,
						Content = "bash",
						ToolName = "bash",
						ToolInput = "git diff --stat",
						CreatedAt = DateTime.UtcNow.AddSeconds(-3)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "first output",
						ToolName = "bash",
						ToolOutput = "first output",
						CreatedAt = DateTime.UtcNow.AddSeconds(-2)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "second output",
						ToolOutput = "second output",
						CreatedAt = DateTime.UtcNow.AddSeconds(-1)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("2 messages", html);
		Assert.Contains("placeholder command", html);
		Assert.Contains("git diff --stat", html);
		Assert.Contains("first output", html);
		Assert.Contains("second output", html);
		Assert.True(
			html.IndexOf("placeholder command", StringComparison.Ordinal)
				< html.IndexOf("first output", StringComparison.Ordinal));
		Assert.True(
			html.IndexOf("first output", StringComparison.Ordinal)
				< html.IndexOf("git diff --stat", StringComparison.Ordinal));
		Assert.True(
			html.IndexOf("git diff --stat", StringComparison.Ordinal)
				< html.IndexOf("second output", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RenderedJobSessionPanel_ExpandsStructuredLiveJsonToolMessages()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var timestamp = DateTime.UtcNow;
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
						Content =
							"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Reviewing the repository state"},{"type":"tool_use","id":"toolu_live_123","name":"bash","input":{"command":"git status --short","description":"Check status"}},{"type":"thinking","thinking":"Checking the modified files before applying a patch."}]}}""",
						Timestamp = timestamp.AddSeconds(-2)
					},
					new()
					{
						Content =
							"""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_live_123","content":"M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor"}]}}""",
						Timestamp = timestamp.AddSeconds(-1)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("3 messages", html);
		Assert.Contains("Reviewing the repository state", html);
		Assert.Contains("Checking the modified files before applying a patch.", html);
		Assert.Contains("Tool Activity", html);
		Assert.Contains("git status --short", html);
		Assert.Contains("bash", html);
		Assert.DoesNotContain("toolu_live_123", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_GroupsThreeConsecutiveToolActivitiesIntoCollapsibleSection()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var timestamp = DateTime.UtcNow;
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
						Role = MessageRole.ToolUse,
						Content = "bash",
						ToolName = "bash",
						ToolInput = "git status --short",
						CreatedAt = timestamp.AddSeconds(-6)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
						ToolName = "bash",
						ToolOutput = "M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
						CreatedAt = timestamp.AddSeconds(-5)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolUse,
						Content = "rg",
						ToolName = "rg",
						ToolInput = "rg ToolUse src",
						CreatedAt = timestamp.AddSeconds(-4)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "src/VibeSwarm.Shared/Data/JobMessage.cs",
						ToolName = "rg",
						ToolOutput = "src/VibeSwarm.Shared/Data/JobMessage.cs",
						CreatedAt = timestamp.AddSeconds(-3)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolUse,
						Content = "dotnet",
						ToolName = "dotnet",
						ToolInput = "dotnet test --filter JobSessionPanel",
						CreatedAt = timestamp.AddSeconds(-2)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "Passed!",
						ToolName = "dotnet",
						ToolOutput = "Passed!",
						CreatedAt = timestamp.AddSeconds(-1)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("3 messages", html);
		Assert.Contains("3 tool calls: bash, rg, dotnet", html);
		Assert.Contains("data-tool-group=\"true\"", html);
		Assert.Contains("git status --short", html);
		Assert.Contains("rg ToolUse src", html);
		Assert.Contains("dotnet test --filter JobSessionPanel", html);
		Assert.DoesNotContain("<details open", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_DoesNotGroupTwoConsecutiveToolActivities()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var timestamp = DateTime.UtcNow;
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
						Role = MessageRole.ToolUse,
						Content = "bash",
						ToolName = "bash",
						ToolInput = "git diff --stat",
						CreatedAt = timestamp.AddSeconds(-4)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "2 files changed",
						ToolName = "bash",
						ToolOutput = "2 files changed",
						CreatedAt = timestamp.AddSeconds(-3)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolUse,
						Content = "rg",
						ToolName = "rg",
						ToolInput = "rg JobSessionPanel src",
						CreatedAt = timestamp.AddSeconds(-2)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "src/VibeSwarm.Tests/JobSessionPanelTests.cs",
						ToolName = "rg",
						ToolOutput = "src/VibeSwarm.Tests/JobSessionPanelTests.cs",
						CreatedAt = timestamp.AddSeconds(-1)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("2 messages", html);
		Assert.DoesNotContain("data-tool-group=\"true\"", html);
		Assert.DoesNotContain("2 tool calls:", html);
		Assert.Equal(2, Regex.Matches(html, "<details>").Count);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_DoesNotGroupToolActivitiesSeparatedByAssistantMessage()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var timestamp = DateTime.UtcNow;
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
						Role = MessageRole.ToolUse,
						Content = "bash",
						ToolName = "bash",
						ToolInput = "git status --short",
						CreatedAt = timestamp.AddSeconds(-7)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
						ToolName = "bash",
						ToolOutput = "M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
						CreatedAt = timestamp.AddSeconds(-6)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.Assistant,
						Content = "I need two more checks before making the change.",
						CreatedAt = timestamp.AddSeconds(-5)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolUse,
						Content = "rg",
						ToolName = "rg",
						ToolInput = "rg ToolUse src",
						CreatedAt = timestamp.AddSeconds(-4)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "src/VibeSwarm.Shared/Data/JobMessage.cs",
						ToolName = "rg",
						ToolOutput = "src/VibeSwarm.Shared/Data/JobMessage.cs",
						CreatedAt = timestamp.AddSeconds(-3)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolUse,
						Content = "dotnet",
						ToolName = "dotnet",
						ToolInput = "dotnet test --filter JobSessionPanel",
						CreatedAt = timestamp.AddSeconds(-2)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.ToolResult,
						Content = "Passed!",
						ToolName = "dotnet",
						ToolOutput = "Passed!",
						CreatedAt = timestamp.AddSeconds(-1)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("4 messages", html);
		Assert.Contains("I need two more checks before making the change.", html);
		Assert.DoesNotContain("data-tool-group=\"true\"", html);
		Assert.DoesNotContain("3 tool calls:", html);
	}

	[Fact]
	public async Task RenderedJobSessionPanel_ShowsTranscriptWindowNoticeWhenMessagesAreCapped()
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
				[nameof(JobSessionPanel.TotalMessageCount)] = 500,
				[nameof(JobSessionPanel.Messages)] = new List<JobMessage>
				{
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.Assistant,
						Content = "Recent message one",
						CreatedAt = DateTime.UtcNow.AddSeconds(-2)
					},
					new()
					{
						Id = Guid.NewGuid(),
						JobId = Guid.NewGuid(),
						Role = MessageRole.Assistant,
						Content = "Recent message two",
						CreatedAt = DateTime.UtcNow.AddSeconds(-1)
					}
				}
			});

			var output = await renderer.RenderComponentAsync<JobSessionPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Showing latest 2 of 500 messages", html);
	}

	[Fact]
	public void JobSessionPanel_Bunit_TogglesCombinedToolActivityFilter()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.Messages, new List<JobMessage>
			{
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.Assistant,
					Content = "Repository review complete.",
					CreatedAt = DateTime.UtcNow.AddSeconds(-3)
				},
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.ToolUse,
					Content = "bash",
					ToolName = "bash",
					ToolInput = "git diff --stat",
					CreatedAt = DateTime.UtcNow.AddSeconds(-2)
				},
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.ToolResult,
					Content = "3 files changed",
					ToolName = "bash",
					ToolOutput = "3 files changed",
					CreatedAt = DateTime.UtcNow.AddSeconds(-1)
				}
			}));

		Assert.Contains("2 messages", cut.Markup);
		Assert.Contains("Tool Activity", cut.Markup);

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Tool Activity", StringComparison.Ordinal))
			.Click();

		Assert.Contains("1 message", cut.Markup);
		Assert.DoesNotContain("git diff --stat", cut.Markup);
		Assert.DoesNotContain("3 files changed", cut.Markup);
		Assert.Contains("Repository review complete.", cut.Markup);
	}

	[Fact]
	public void JobSessionPanel_Bunit_TogglesGroupedToolActivityFilter()
	{
		using var context = new BunitContext();

		var timestamp = DateTime.UtcNow;
		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.Messages, new List<JobMessage>
			{
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.Assistant,
					Content = "Repository review complete.",
					CreatedAt = timestamp.AddSeconds(-7)
				},
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.ToolUse,
					Content = "bash",
					ToolName = "bash",
					ToolInput = "git status --short",
					CreatedAt = timestamp.AddSeconds(-6)
				},
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.ToolResult,
					Content = "M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
					ToolName = "bash",
					ToolOutput = "M src/VibeSwarm.Client/Components/Jobs/JobSessionPanel.razor",
					CreatedAt = timestamp.AddSeconds(-5)
				},
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.ToolUse,
					Content = "rg",
					ToolName = "rg",
					ToolInput = "rg ToolUse src",
					CreatedAt = timestamp.AddSeconds(-4)
				},
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.ToolResult,
					Content = "src/VibeSwarm.Shared/Data/JobMessage.cs",
					ToolName = "rg",
					ToolOutput = "src/VibeSwarm.Shared/Data/JobMessage.cs",
					CreatedAt = timestamp.AddSeconds(-3)
				},
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.ToolUse,
					Content = "dotnet",
					ToolName = "dotnet",
					ToolInput = "dotnet test --filter JobSessionPanel",
					CreatedAt = timestamp.AddSeconds(-2)
				},
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.ToolResult,
					Content = "Passed!",
					ToolName = "dotnet",
					ToolOutput = "Passed!",
					CreatedAt = timestamp.AddSeconds(-1)
				}
			}));

		Assert.Contains("4 messages", cut.Markup);
		Assert.Contains("3 tool calls: bash, rg, dotnet", cut.Markup);
		Assert.Contains("data-tool-group=\"true\"", cut.Markup);

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Tool Activity", StringComparison.Ordinal))
			.Click();

		Assert.Contains("1 message", cut.Markup);
		Assert.DoesNotContain("3 tool calls: bash, rg, dotnet", cut.Markup);
		Assert.DoesNotContain("git status --short", cut.Markup);
		Assert.DoesNotContain("rg ToolUse src", cut.Markup);
		Assert.DoesNotContain("dotnet test --filter JobSessionPanel", cut.Markup);
		Assert.Contains("Repository review complete.", cut.Markup);
	}

	[Fact]
	public void JobSessionPanel_Bunit_SwitchesTabsAndCopiesCommand()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("copyToClipboard", "dotnet test");

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.Messages, new List<JobMessage>
			{
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.Assistant,
					Content = "Completed successfully.",
					CreatedAt = DateTime.UtcNow
				}
			})
			.Add(panel => panel.ConsoleOutput, "Build verified")
			.Add(panel => panel.CommandUsed, "dotnet test")
			.Add(panel => panel.SessionId, "session-123"));

		cut.FindAll("button[role='tab']")
			.Single(button => button.TextContent.Contains("Console", StringComparison.Ordinal))
			.Click();

		Assert.Contains("Build verified", cut.Markup);

		cut.FindAll("button[role='tab']")
			.Single(button => button.TextContent.Contains("Command", StringComparison.Ordinal))
			.Click();

		cut.Find("button[title='Copy command to clipboard']").Click();

		var invocation = Assert.Single(context.JSInterop.Invocations);
		Assert.Equal("copyToClipboard", invocation.Identifier);
		Assert.Equal("dotnet test", invocation.Arguments[0]?.ToString());
		Assert.Contains("session-123", cut.Markup);
	}

	[Fact]
	public void JobSessionPanel_Bunit_RendersStepAccordionsForPlanningAndExecutionCommands()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("copyToClipboard", "copilot --plan");
		context.JSInterop.SetupVoid("copyToClipboard", "copilot --run");

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.Messages, new List<JobMessage>
			{
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.Assistant,
					Content = "Completed successfully.",
					CreatedAt = DateTime.UtcNow
				}
			})
			.Add(panel => panel.PlanningCommandUsed, "copilot --plan")
			.Add(panel => panel.ExecutionCommandUsed, "copilot --run"));

		cut.FindAll("button[role='tab']")
			.Single(button => button.TextContent.Contains("Command", StringComparison.Ordinal))
			.Click();

		Assert.Contains("accordion", cut.Markup);
		Assert.Contains("Planning", cut.Markup);
		Assert.Contains("Execution", cut.Markup);
		Assert.Contains("copilot --plan", cut.Markup);
		Assert.Contains("copilot --run", cut.Markup);
	}

	[Fact]
	public void JobSessionPanel_Bunit_UsesExecutionFallbackWhenStepCommandsAreUnavailable()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("copyToClipboard", "dotnet test");

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.Messages, new List<JobMessage>
			{
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.Assistant,
					Content = "Completed successfully.",
					CreatedAt = DateTime.UtcNow
				}
			})
			.Add(panel => panel.CommandUsed, "dotnet test"));

		cut.FindAll("button[role='tab']")
			.Single(button => button.TextContent.Contains("Command", StringComparison.Ordinal))
			.Click();

		Assert.DoesNotContain("accordion-item", cut.Markup);
		Assert.Contains("dotnet test", cut.Markup);

		cut.Find("button[title='Copy command to clipboard']").Click();

		var invocation = Assert.Single(context.JSInterop.Invocations);
		Assert.Equal("dotnet test", invocation.Arguments[0]?.ToString());
	}

	[Fact]
	public void JobSessionPanel_Bunit_CallsClearLiveOutputCallback()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("vibeSwarmLiveOutput.sync", _ => true);
		var cleared = false;

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Processing)
			.Add(panel => panel.IsJobActive, true)
			.Add(panel => panel.LiveOutputLines, new List<OutputLine>
			{
				new()
				{
					Content = "[Assistant] Reviewing repository changes",
					Timestamp = DateTime.UtcNow
				}
			})
			.Add(panel => panel.OnClearLiveOutput, async () =>
			{
				cleared = true;
				await Task.CompletedTask;
			}));

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Clear", StringComparison.Ordinal))
			.Click();

		Assert.True(cleared);
	}

	[Fact]
	public void JobSessionPanel_Bunit_RendersGoalPromptBubbleWithDropdownActions()
	{
		using var context = new BunitContext();
		var copied = false;

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.GoalPrompt, "Implement the feature exactly as described.")
			.Add(panel => panel.GoalPromptIdeaText, "Ship the requested feature.")
			.Add(panel => panel.GoalPromptTimestamp, DateTime.UtcNow)
			.Add(panel => panel.OnCopyGoalPrompt, async () =>
			{
				copied = true;
				await Task.CompletedTask;
			}));

		Assert.Contains("Messages", cut.Markup);
		Assert.Contains("1 message", cut.Markup);
		Assert.Contains("Ship the requested feature.", cut.Markup);
		Assert.Contains("Goal", cut.Markup);
		Assert.DoesNotContain("overflow-hidden", cut.Find(".card-body").ClassName);
		Assert.Equal("static", cut.Find("button[title='Goal prompt actions']").GetAttribute("data-bs-display"));

		cut.Find("button[title='Copy prompt']").Click();

		Assert.True(copied);

		cut.Find("button[title='View full prompt']").Click();

		Assert.Contains("Full Goal Prompt", cut.Markup);
		Assert.Contains("Implement the feature exactly as described.", cut.Markup);
	}

	[Fact]
	public void JobSessionPanel_Bunit_RendersAttachedFilesSection()
	{
		using var context = new BunitContext();

		var attachmentId = Guid.NewGuid();
		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.GoalPrompt, "Implement the feature exactly as described.")
			.Add(panel => panel.AttachedIdeaFiles, new List<IdeaAttachment>
			{
				new()
				{
					Id = attachmentId,
					FileName = "screenshot.png",
					ContentType = "image/png",
					RelativePath = Path.Combine(".vibeswarm", "idea-attachments", "screenshot.png"),
					SizeBytes = 4096
				}
			}));

		Assert.Contains("Attached files", cut.Markup);
		Assert.Contains("screenshot.png", cut.Markup);
		Assert.Contains($"/api/ideas/attachments/{attachmentId}", cut.Markup);
		Assert.Contains("4 KB", cut.Markup);
	}

	[Fact]
	public void JobSessionPanel_Bunit_AllowsEditingGoalPromptFromBubbleMenu()
	{
		using var context = new BunitContext();
		string? savedPrompt = null;

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Failed)
			.Add(panel => panel.GoalPrompt, "Original prompt")
			.Add(panel => panel.OnGoalPromptChanged, async value =>
			{
				savedPrompt = value;
				await Task.CompletedTask;
			}));

		cut.Find("button[title='Edit prompt']").Click();

		var textarea = cut.Find("textarea");
		textarea.Change("Updated prompt");
		cut.Find("button[title='Save changes']").Click();

		Assert.Equal("Updated prompt", savedPrompt);
	}

	[Fact]
	public void JobSessionPanel_Bunit_AutoScrollsActiveSessionWhenLiveOutputChanges()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("vibeSwarmLiveOutput.sync", _ => true);

		var firstTimestamp = DateTime.UtcNow;
		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Processing)
			.Add(panel => panel.IsJobActive, true)
			.Add(panel => panel.LiveOutputLines, new List<OutputLine>
			{
				new()
				{
					Content = "[Assistant] Reviewing repository changes",
					Timestamp = firstTimestamp
				}
			}));

		Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "vibeSwarmLiveOutput.sync");

		context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Processing)
			.Add(panel => panel.IsJobActive, true)
			.Add(panel => panel.LiveOutputLines, new List<OutputLine>
			{
				new()
				{
					Content = "[Assistant] Reviewing repository changes",
					Timestamp = firstTimestamp
				},
				new()
				{
					Content = "[Assistant] Applying the fix",
					Timestamp = firstTimestamp.AddSeconds(1)
				}
			}));

		Assert.Equal(2, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "vibeSwarmLiveOutput.sync"));
	}

	[Fact]
	public void JobSessionPanel_Bunit_DoesNotAutoScrollInactiveOrHiddenSessionView()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("copyToClipboard", "dotnet test");
		context.JSInterop.SetupVoid("vibeSwarmLiveOutput.sync", _ => true);

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.Messages, new List<JobMessage>
			{
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.Assistant,
					Content = "Completed successfully.",
					CreatedAt = DateTime.UtcNow
				}
			})
			.Add(panel => panel.ConsoleOutput, "Build verified")
			.Add(panel => panel.CommandUsed, "dotnet test"));

		Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "vibeSwarmLiveOutput.sync");

		cut.FindAll("button[role='tab']")
			.Single(button => button.TextContent.Contains("Command", StringComparison.Ordinal))
			.Click();

		Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "vibeSwarmLiveOutput.sync");
	}

	[Fact]
	public void JobSessionPanel_Bunit_SendsFollowUpAndClearsDraft()
	{
		using var context = new BunitContext();
		string? followUpPrompt = null;

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Completed)
			.Add(panel => panel.Messages, new List<JobMessage>
			{
				new()
				{
					Id = Guid.NewGuid(),
					JobId = Guid.NewGuid(),
					Role = MessageRole.Assistant,
					Content = "Ready for the next instruction.",
					CreatedAt = DateTime.UtcNow
				}
			})
			.Add(panel => panel.OnSendFollowUp, async (string prompt) =>
			{
				followUpPrompt = prompt;
				await Task.CompletedTask;
			}));

		var sendButton = cut.Find("button[title='Send follow-up']");
		Assert.True(sendButton.HasAttribute("disabled"));

		cut.Find("textarea").Input("Please add one more assertion.");

		sendButton = cut.Find("button[title='Send follow-up']");
		Assert.False(sendButton.HasAttribute("disabled"));

		sendButton.Click();

		Assert.Equal("Please add one more assertion.", followUpPrompt);
		Assert.True(cut.Find("button[title='Send follow-up']").HasAttribute("disabled"));
	}

	[Fact]
	public void JobSessionPanel_Bunit_ShowsFollowUpInputForStalledJob()
	{
		using var context = new BunitContext();
		string? followUpPrompt = null;

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Stalled)
			.Add(panel => panel.Messages, new List<JobMessage>())
			.Add(panel => panel.OnSendFollowUp, async (string prompt) =>
			{
				followUpPrompt = prompt;
				await Task.CompletedTask;
			}));

		var sendButton = cut.Find("button[title='Send follow-up']");
		Assert.True(sendButton.HasAttribute("disabled"));

		cut.Find("textarea").Input("Try again with the auth middleware.");

		sendButton = cut.Find("button[title='Send follow-up']");
		Assert.False(sendButton.HasAttribute("disabled"));

		sendButton.Click();

		Assert.Equal("Try again with the auth middleware.", followUpPrompt);
	}

	[Fact]
	public void JobSessionPanel_Bunit_ShowsFollowUpInputForFailedJob()
	{
		using var context = new BunitContext();
		string? followUpPrompt = null;

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Failed)
			.Add(panel => panel.Messages, new List<JobMessage>())
			.Add(panel => panel.OnSendFollowUp, async (string prompt) =>
			{
				followUpPrompt = prompt;
				await Task.CompletedTask;
			}));

		var sendButton = cut.Find("button[title='Send follow-up']");
		Assert.True(sendButton.HasAttribute("disabled"));

		cut.Find("textarea").Input("Fix the null reference on line 42.");

		sendButton = cut.Find("button[title='Send follow-up']");
		Assert.False(sendButton.HasAttribute("disabled"));

		sendButton.Click();

		Assert.Equal("Fix the null reference on line 42.", followUpPrompt);
	}

	[Fact]
	public void JobSessionPanel_Bunit_HidesFollowUpInputForActiveJob()
	{
		using var context = new BunitContext();

		var cut = context.Render<JobSessionPanel>(parameters => parameters
			.Add(panel => panel.Status, JobStatus.Processing)
			.Add(panel => panel.Messages, new List<JobMessage>())
			.Add(panel => panel.OnSendFollowUp, async (string prompt) => await Task.CompletedTask));

		Assert.Empty(cut.FindAll("button[title='Send follow-up']"));
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
