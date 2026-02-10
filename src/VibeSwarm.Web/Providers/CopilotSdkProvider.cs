using System.Text;
using GitHub.Copilot.SDK;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Copilot provider using the official GitHub.Copilot.SDK NuGet package.
/// Uses CopilotClient → CopilotSession for typed, structured communication
/// with streaming events, tool tracking, and proper lifecycle management.
/// </summary>
public class CopilotSdkProvider : SdkProviderBase
{
	private CopilotClient? _client;
	private const string DefaultModel = "gpt-4o";

	public override ProviderType Type => ProviderType.Copilot;

	public CopilotSdkProvider(Provider config) : base(config) { }

	/// <summary>
	/// Builds the CopilotClientOptions from provider configuration.
	/// </summary>
	private CopilotClientOptions BuildClientOptions(string? workingDirectory = null)
	{
		var options = new CopilotClientOptions
		{
			AutoStart = true,
			AutoRestart = false,
			UseStdio = true,
			LogLevel = "warn"
		};

		if (!string.IsNullOrEmpty(ExecutablePath))
		{
			options.CliPath = ExecutablePath;
		}

		var cwd = workingDirectory ?? WorkingDirectory;
		if (!string.IsNullOrEmpty(cwd))
		{
			options.Cwd = cwd;
		}

		if (!string.IsNullOrEmpty(ApiKey))
		{
			options.GithubToken = ApiKey;
		}

		if (CurrentEnvironmentVariables is { Count: > 0 })
		{
			options.Environment = new Dictionary<string, string>(CurrentEnvironmentVariables);
		}

		return options;
	}

	/// <summary>
	/// Ensures a CopilotClient is created and started.
	/// </summary>
	private async Task<CopilotClient> EnsureClientAsync(
		string? workingDirectory = null,
		CancellationToken cancellationToken = default)
	{
		if (_client != null) return _client;

		var options = BuildClientOptions(workingDirectory);
		_client = new CopilotClient(options);
		await _client.StartAsync();
		return _client;
	}

	/// <summary>
	/// Resolves model string, falling back to default.
	/// </summary>
	private string ResolveModel()
	{
		return !string.IsNullOrEmpty(CurrentModel) ? CurrentModel : DefaultModel;
	}

	public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var client = await EnsureClientAsync(cancellationToken: cancellationToken);
			var ping = await client.PingAsync();

			IsConnected = ping != null;
			LastConnectionError = IsConnected ? null : "Copilot SDK ping returned null.";
			return IsConnected;
		}
		catch (Exception ex)
		{
			IsConnected = false;
			LastConnectionError = $"Failed to connect to Copilot SDK: {ex.Message}";
			return false;
		}
	}

	public override async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
	{
		var client = await EnsureClientAsync(cancellationToken: cancellationToken);

		var sessionConfig = new SessionConfig { Model = ResolveModel() };
		await using var session = await client.CreateSessionAsync(sessionConfig);

		var responseBuilder = new StringBuilder();
		var done = new TaskCompletionSource();

		using var _ = session.On(evt =>
		{
			switch (evt)
			{
				case AssistantMessageEvent msg:
					responseBuilder.Append(msg.Data.Content);
					break;
				case SessionIdleEvent:
					done.TrySetResult();
					break;
				case SessionErrorEvent err:
					done.TrySetException(new InvalidOperationException(
						err.Data.Message ?? "Unknown Copilot session error"));
					break;
			}
		});

		await session.SendAsync(new MessageOptions { Prompt = prompt });

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(TimeSpan.FromMinutes(10));
		using var reg = cts.Token.Register(() => done.TrySetCanceled());

		await done.Task;

		return responseBuilder.ToString();
	}

	public override async Task<ExecutionResult> ExecuteWithSessionAsync(
		string prompt,
		string? sessionId = null,
		string? workingDirectory = null,
		IProgress<ExecutionProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		var result = new ExecutionResult { Messages = new List<ExecutionMessage>() };
		var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;
		var model = ResolveModel();

		try
		{
			progress?.Report(new ExecutionProgress
			{
				CurrentMessage = "Starting Copilot SDK session...",
				IsStreaming = false
			});

			var client = await EnsureClientAsync(effectiveWorkingDir, cancellationToken);

			result.CommandUsed = $"Copilot SDK ({model})";

			// Build session config
			var sessionConfig = new SessionConfig
			{
				Model = model,
				Streaming = true
			};

			if (!string.IsNullOrEmpty(sessionId))
			{
				sessionConfig.SessionId = sessionId;
			}

			// Apply working directory to session config
			sessionConfig.WorkingDirectory = effectiveWorkingDir;

			// Create or resume session
			CopilotSession session;
			if (!string.IsNullOrEmpty(sessionId))
			{
				try
				{
					var resumeConfig = new ResumeSessionConfig
					{
						Model = model,
						Streaming = true,
						WorkingDirectory = effectiveWorkingDir
					};
					session = await client.ResumeSessionAsync(sessionId, resumeConfig, cancellationToken);
				}
				catch
				{
					session = await client.CreateSessionAsync(sessionConfig);
				}
			}
			else
			{
				session = await client.CreateSessionAsync(sessionConfig);
			}

			await using (session)
			{
				result.SessionId = session.SessionId;

				var contentBuilder = new StringBuilder();
				var outputBuilder = new StringBuilder();
				var done = new TaskCompletionSource();
				string? currentToolName = null;

				progress?.Report(new ExecutionProgress
				{
					CurrentMessage = $"Connected. Session: {session.SessionId}",
					IsStreaming = false
				});

				// Subscribe to all session events
				using var subscription = session.On(evt =>
				{
					try
					{
						ProcessSessionEvent(evt, result, contentBuilder, outputBuilder,
							ref currentToolName, progress, done);
					}
					catch (Exception ex)
					{
						// Don't let event handler exceptions kill the subscription
						result.ErrorMessage ??= $"Event processing error: {ex.Message}";
					}
				});

				// Send the prompt
				await session.SendAsync(new MessageOptions { Prompt = prompt });

				// Wait for session idle or cancellation
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				cts.CancelAfter(TimeSpan.FromMinutes(30));
				using var reg = cts.Token.Register(() =>
				{
					done.TrySetCanceled();
					try { session.AbortAsync().GetAwaiter().GetResult(); } catch { }
				});

				try
				{
					await done.Task;
				}
				catch (OperationCanceledException)
				{
					result.Success = false;
					result.ErrorMessage = "Execution was cancelled.";
					return result;
				}

				// Flush any remaining content
				if (contentBuilder.Length > 0)
				{
					result.Messages.Add(new ExecutionMessage
					{
						Role = "assistant",
						Content = contentBuilder.ToString(),
						Timestamp = DateTime.UtcNow
					});
				}

				result.Success = string.IsNullOrEmpty(result.ErrorMessage);
				result.Output = outputBuilder.ToString();
				result.ModelUsed ??= model;
			}
		}
		catch (OperationCanceledException)
		{
			result.Success = false;
			result.ErrorMessage = "Execution was cancelled.";
		}
		catch (Exception ex)
		{
			result.Success = false;
			result.ErrorMessage = $"Copilot SDK error: {ex.Message}";
		}

		return result;
	}

	/// <summary>
	/// Processes a typed SDK session event, populating the result
	/// and streaming progress to the UI.
	/// </summary>
	private static void ProcessSessionEvent(
		SessionEvent evt,
		ExecutionResult result,
		StringBuilder contentBuilder,
		StringBuilder outputBuilder,
		ref string? currentToolName,
		IProgress<ExecutionProgress>? progress,
		TaskCompletionSource done)
	{
		switch (evt)
		{
			case AssistantMessageDeltaEvent delta:
				{
					var chunk = delta.Data.DeltaContent;
					if (!string.IsNullOrEmpty(chunk))
					{
						contentBuilder.Append(chunk);
						outputBuilder.AppendLine(chunk);

						progress?.Report(new ExecutionProgress
						{
							OutputLine = chunk,
							IsStreaming = true
						});
					}
					break;
				}

			case AssistantMessageEvent msg:
				{
					// Final assistant message — flush accumulated content
					var finalContent = !string.IsNullOrEmpty(msg.Data.Content)
						? msg.Data.Content
						: contentBuilder.ToString();

					if (!string.IsNullOrEmpty(finalContent))
					{
						result.Messages.Add(new ExecutionMessage
						{
							Role = "assistant",
							Content = finalContent,
							Timestamp = DateTime.UtcNow
						});
						contentBuilder.Clear();
						outputBuilder.AppendLine(finalContent);

						progress?.Report(new ExecutionProgress
						{
							OutputLine = $"[Assistant] {(finalContent.Length > 200 ? finalContent[..200] + "..." : finalContent)}",
							IsStreaming = false
						});
					}
					break;
				}

			case ToolExecutionStartEvent toolStart:
				{
					var toolName = toolStart.Data.ToolName ?? "unknown_tool";
					var toolInput = toolStart.Data.Arguments?.ToString();
					currentToolName = toolName;

					result.Messages.Add(new ExecutionMessage
					{
						Role = "tool_use",
						Content = toolName,
						ToolName = toolName,
						ToolInput = toolInput,
						Timestamp = DateTime.UtcNow
					});

					var displayLine = $"[Tool] {toolName}";
					if (!string.IsNullOrEmpty(toolInput))
					{
						var truncatedInput = toolInput.Length > 150 ? toolInput[..150] + "..." : toolInput;
						displayLine += $": {truncatedInput}";
					}
					outputBuilder.AppendLine(displayLine);

					progress?.Report(new ExecutionProgress
					{
						OutputLine = displayLine,
						ToolName = toolName,
						IsStreaming = false
					});
					break;
				}

			case ToolExecutionCompleteEvent toolComplete:
				{
					var toolName = currentToolName ?? "unknown_tool";
					var toolOutput = toolComplete.Data.Result?.Content;

					result.Messages.Add(new ExecutionMessage
					{
						Role = "tool_result",
						Content = toolOutput ?? "",
						ToolName = toolName,
						ToolOutput = toolOutput,
						Timestamp = DateTime.UtcNow
					});

					var truncatedOutput = string.IsNullOrEmpty(toolOutput)
						? "(no output)"
						: (toolOutput.Length > 200 ? toolOutput[..200] + "..." : toolOutput);

					var displayLine = $"[Tool Result] {toolName}: {truncatedOutput}";
					outputBuilder.AppendLine(displayLine);

					progress?.Report(new ExecutionProgress
					{
						OutputLine = displayLine,
						IsStreaming = false
					});

					currentToolName = null;
					break;
				}

			case SessionIdleEvent:
				{
					progress?.Report(new ExecutionProgress
					{
						CurrentMessage = "Session complete.",
						OutputLine = "[Session] Complete",
						IsStreaming = false
					});
					done.TrySetResult();
					break;
				}

			case SessionErrorEvent error:
				{
					var errorMsg = error.Data.Message ?? "Unknown session error";
					result.ErrorMessage = errorMsg;

					progress?.Report(new ExecutionProgress
					{
						OutputLine = $"[Error] {errorMsg}",
						IsErrorOutput = true,
						IsStreaming = false
					});
					done.TrySetResult();
					break;
				}

			case AssistantUsageEvent usage:
				{
					result.ModelUsed = usage.Data.Model ?? result.ModelUsed;
					if (usage.Data.InputTokens.HasValue)
						result.InputTokens = (result.InputTokens ?? 0) + (int)usage.Data.InputTokens.Value;
					if (usage.Data.OutputTokens.HasValue)
						result.OutputTokens = (result.OutputTokens ?? 0) + (int)usage.Data.OutputTokens.Value;
					if (usage.Data.Cost.HasValue)
						result.CostUsd = (result.CostUsd ?? 0) + (decimal)usage.Data.Cost.Value;

					progress?.Report(new ExecutionProgress
					{
						TokensUsed = (result.InputTokens ?? 0) + (result.OutputTokens ?? 0),
						IsStreaming = false
					});
					break;
				}

			case SessionShutdownEvent shutdown:
				{
					if (shutdown.Data.TotalPremiumRequests > 0)
					{
						result.PremiumRequestsConsumed = (int)shutdown.Data.TotalPremiumRequests;
					}
					break;
				}

			case AssistantReasoningDeltaEvent reasoning:
				{
					if (!string.IsNullOrEmpty(reasoning.Data.DeltaContent))
					{
						progress?.Report(new ExecutionProgress
						{
							OutputLine = $"[Reasoning] {reasoning.Data.DeltaContent}",
							IsStreaming = true
						});
					}
					break;
				}

			case ToolExecutionProgressEvent toolProgress:
				{
					if (!string.IsNullOrEmpty(toolProgress.Data.ProgressMessage))
					{
						progress?.Report(new ExecutionProgress
						{
							OutputLine = toolProgress.Data.ProgressMessage,
							IsStreaming = false
						});
					}
					break;
				}

			default:
				{
					// Log other events as informational output (e.g., reasoning, compaction)
					var evtType = evt.GetType().Name.Replace("Event", "");
					if (!string.IsNullOrEmpty(evtType) && evtType != "Session")
					{
						progress?.Report(new ExecutionProgress
						{
							OutputLine = $"[{evtType}]",
							IsStreaming = false
						});
					}
					break;
				}
		}
	}

	public override async Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default)
	{
		var info = new ProviderInfo
		{
			Version = "SDK (GitHub.Copilot.SDK)",
			AvailableModels = new List<string>
			{
				"gpt-4o",
				"gpt-5",
				"claude-sonnet-4.5",
				"o3-mini"
			},
			AvailableAgents = new List<AgentInfo>
			{
				new() { Name = "default", Description = "GitHub Copilot SDK agent", IsDefault = true }
			},
			Pricing = new PricingInfo
			{
				Currency = "USD"
			},
			AdditionalInfo = new Dictionary<string, object>
			{
				["isAvailable"] = true,
				["connectionMode"] = "SDK"
			}
		};

		// Try to verify connection and fetch models
		try
		{
			var client = await EnsureClientAsync(cancellationToken: cancellationToken);
			var ping = await client.PingAsync(cancellationToken: cancellationToken);
			info.AdditionalInfo["isAvailable"] = ping != null;

			if (ping != null)
			{
				var status = await client.GetStatusAsync(cancellationToken);
				if (status != null)
				{
					info.Version = $"SDK (v{status.Version})";
				}

				var models = await client.ListModelsAsync(cancellationToken);
				if (models?.Count > 0)
				{
					info.AvailableModels = models.Select(m => m.Id).ToList();
				}
			}
		}
		catch
		{
			info.AdditionalInfo["isAvailable"] = false;
		}

		return info;
	}

	public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult(new UsageLimits
		{
			LimitType = UsageLimitType.PremiumRequests,
			IsLimitReached = false,
			Message = "Premium request usage tracked per-session via Copilot SDK"
		});
	}

	public override async Task<SessionSummary> GetSessionSummaryAsync(
		string? sessionId,
		string? workingDirectory = null,
		string? fallbackOutput = null,
		CancellationToken cancellationToken = default)
	{
		var summary = new SessionSummary();

		// Try to retrieve session messages via SDK if we have a session ID
		if (!string.IsNullOrEmpty(sessionId) && _client != null)
		{
			try
			{
				var config = new ResumeSessionConfig();
				var session = await _client.ResumeSessionAsync(sessionId, config, cancellationToken);
				await using (session)
				{
					var messages = await session.GetMessagesAsync();
					if (messages.Count > 0)
					{
						var sb = new StringBuilder();
						foreach (var msg in messages)
						{
							if (msg is AssistantMessageEvent assistantMsg)
							{
								sb.AppendLine(assistantMsg.Data.Content);
							}
						}

						if (sb.Length > 0)
						{
							summary.Summary = OutputSummaryHelper.GenerateSummaryFromOutput(sb.ToString());
							summary.Success = true;
							summary.Source = "sdk_session";
							return summary;
						}
					}
				}
			}
			catch
			{
				// Fall through to fallback
			}
		}

		if (!string.IsNullOrEmpty(fallbackOutput))
		{
			summary.Summary = OutputSummaryHelper.GenerateSummaryFromOutput(fallbackOutput);
			summary.Success = !string.IsNullOrEmpty(summary.Summary);
			summary.Source = "output";
			return summary;
		}

		summary.Success = false;
		summary.ErrorMessage = "No output available to generate summary.";
		return summary;
	}

	public override async Task<PromptResponse> GetPromptResponseAsync(
		string prompt,
		string? workingDirectory = null,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		try
		{
			var client = await EnsureClientAsync(workingDirectory, cancellationToken);
			var model = ResolveModel();

			var sessionConfig = new SessionConfig { Model = model };
			await using var session = await client.CreateSessionAsync(sessionConfig);

			var responseBuilder = new StringBuilder();
			var done = new TaskCompletionSource();

			using var subscription = session.On(evt =>
			{
				switch (evt)
				{
					case AssistantMessageEvent msg:
						responseBuilder.Append(msg.Data.Content);
						break;
					case SessionIdleEvent:
						done.TrySetResult();
						break;
					case SessionErrorEvent err:
						done.TrySetException(new InvalidOperationException(
							err.Data.Message ?? "Unknown error"));
						break;
				}
			});

			await session.SendAsync(new MessageOptions { Prompt = prompt });

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(TimeSpan.FromMinutes(5));
			using var reg = cts.Token.Register(() => done.TrySetCanceled());

			await done.Task;

			stopwatch.Stop();
			return PromptResponse.Ok(responseBuilder.ToString(), stopwatch.ElapsedMilliseconds, model);
		}
		catch (OperationCanceledException)
		{
			return PromptResponse.Fail("Request was cancelled or timed out.");
		}
		catch (Exception ex)
		{
			return PromptResponse.Fail($"Copilot SDK error: {ex.Message}");
		}
	}

	public override async ValueTask DisposeAsync()
	{
		if (_client != null)
		{
			try
			{
				await _client.StopAsync();
			}
			catch
			{
				try { await _client.ForceStopAsync(); } catch { }
			}
			finally
			{
				await _client.DisposeAsync();
				_client = null;
			}
		}

		GC.SuppressFinalize(this);
	}
}
