using System.Text;
using GitHub.Copilot;
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
	/// <summary>
	/// Only for a custom (BYOK) endpoint, which needs a model named. Copilot itself picks its own
	/// default when none is set.
	/// </summary>
	private const string DefaultByokModel = "gpt-4o";
	private UsageLimits? _lastObservedUsageLimits;
	private static readonly string[] FallbackAvailableModels =
	[
		"claude-opus-4.7",
		"claude-sonnet-4.6",
		"claude-sonnet-4.5",
		"claude-haiku-4.5",
		"claude-opus-4.6",
		"claude-opus-4.6-fast",
		"claude-opus-4.5",
		"claude-sonnet-4",
		"gpt-5.4",
		"gpt-5.3-codex",
		"gpt-5.2-codex",
		"gpt-5.2",
		"gpt-5.1",
		"gpt-5.4-mini",
		"gpt-5-mini",
		"gpt-4.1"
	];

	private static readonly Dictionary<string, DateTime> UpstreamRetirementDates = new()
	{
		["claude-sonnet-4"] = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
	};

	public override ProviderType Type => ProviderType.Copilot;
	internal static TimeSpan DefaultPromptTimeout => JobCompletionCriteria.DefaultStallTimeoutValue;

	public CopilotSdkProvider(Provider config) : base(config) { }

	internal static IReadOnlyList<string> GetFallbackAvailableModels() => FallbackAvailableModels;

	internal static void ApplySessionDefaults(SessionConfig sessionConfig)
	{
		sessionConfig.OnPermissionRequest = PermissionHandler.ApproveAll;
	}

	internal static void ApplyResumeSessionDefaults(ResumeSessionConfig sessionConfig)
	{
		sessionConfig.OnPermissionRequest = PermissionHandler.ApproveAll;
	}

	/// <summary>
	/// Builds the CopilotClientOptions from provider configuration.
	/// </summary>
	internal CopilotClientOptions BuildClientOptions(string? workingDirectory = null)
	{
		var useCustomProvider = !string.IsNullOrEmpty(ApiEndpoint);
		var options = new CopilotClientOptions
		{
			LogLevel = CopilotLogLevel.Warning,
			UseLoggedInUser = !useCustomProvider && string.IsNullOrEmpty(ApiKey),
			Connection = BuildRuntimeConnection(ExecutablePath, BuildStartupCliArgs())
		};

		var cwd = workingDirectory ?? WorkingDirectory;
		if (!string.IsNullOrEmpty(cwd))
		{
			options.WorkingDirectory = cwd;
		}

		if (!useCustomProvider && !string.IsNullOrEmpty(ApiKey))
		{
			options.GitHubToken = ApiKey;
		}

		if (BuildStartupEnvironmentVariables() is { Count: > 0 } environmentVariables)
		{
			options.Environment = environmentVariables;
		}

		return options;
	}

	/// <summary>
	/// Points the SDK at the host's Copilot CLI. VibeSwarm opts out of the SDK's bundled runtime
	/// (CopilotSkipCliDownload), so every connection needs an explicit executable.
	/// </summary>
	internal static RuntimeConnection BuildRuntimeConnection(string? executablePath, IList<string>? cliArgs = null)
	{
		var configuredPath = string.IsNullOrEmpty(executablePath)
			? null
			: Path.IsPathRooted(executablePath) ? executablePath : Path.GetFullPath(executablePath);
		var cliPath = PlatformHelper.ResolveExecutablePath(
			OperatingSystem.IsWindows() ? "copilot.exe" : "copilot",
			configuredPath,
			PlatformHelper.GetEnhancedPath());

		return RuntimeConnection.ForStdio(cliPath, cliArgs is { Count: > 0 } ? cliArgs : null!);
	}

	internal List<string>? BuildStartupCliArgs()
	{
		List<string>? args = null;

		if (!string.IsNullOrWhiteSpace(CurrentBashEnvPath))
		{
			args ??= [];
			args.Add("--bash-env");
			args.Add("on");
		}

		if (!string.IsNullOrWhiteSpace(CurrentMcpConfigPath))
		{
			args ??= [];
			args.Add("--additional-mcp-config");
			args.Add($"@{CurrentMcpConfigPath}");
		}

		if (CurrentAdditionalDirectories is { Count: > 0 })
		{
			foreach (var dir in CurrentAdditionalDirectories
				.Where(static dir => !string.IsNullOrWhiteSpace(dir))
				.Select(static dir => dir.Trim())
				.Distinct(StringComparer.Ordinal))
			{
				args ??= [];
				args.Add("--add-dir");
				args.Add(dir);
			}
		}

		return args;
	}

	private Dictionary<string, string>? BuildStartupEnvironmentVariables()
	{
		var environmentVariables = GetEffectiveEnvironmentVariables();
		if (string.IsNullOrWhiteSpace(CurrentBashEnvPath))
		{
			return environmentVariables;
		}

		environmentVariables = environmentVariables != null
			? new Dictionary<string, string>(environmentVariables, StringComparer.Ordinal)
			: new Dictionary<string, string>(StringComparer.Ordinal);

		environmentVariables["BASH_ENV"] = CurrentBashEnvPath;
		return environmentVariables;
	}

	/// <summary>
	/// Ensures a CopilotClient is created and started.
	/// If a previous client exists but is unhealthy, it is disposed and recreated.
	/// </summary>
	/// <summary>
	/// Progress reporter for connection state changes, set during ExecuteWithSessionAsync.
	/// </summary>

	private async Task<CopilotClient> EnsureClientAsync(
		string? workingDirectory = null,
		CancellationToken cancellationToken = default)
	{
		if (_client != null) return _client;

		var options = BuildClientOptions(workingDirectory);
		var client = new CopilotClient(options);
		try
		{
			await client.StartAsync(cancellationToken);

			_client = client;
			return _client;
		}
		catch
		{
			try { await client.DisposeAsync(); } catch { }
			throw;
		}
	}

	/// <summary>
	/// Disposes the current client so the next call to EnsureClientAsync creates a fresh one.
	/// </summary>
	private async Task ResetClientAsync()
	{
		var client = _client;
		_client = null;
		if (client != null)
		{
			try { await client.StopAsync(); } catch { }
			try { await client.DisposeAsync(); } catch { }
		}
	}

	/// <summary>
	/// The model to request, or null to let Copilot choose.
	/// </summary>
	private string? ResolveModel()
	{
		if (!string.IsNullOrEmpty(CurrentModel))
		{
			return CurrentModel;
		}

		return string.IsNullOrEmpty(ApiEndpoint) ? null : DefaultByokModel;
	}

	/// <summary>
	/// Copilot's own names for the tools planning blocks (given the Claude Code way: Bash, Edit).
	/// </summary>
	internal static IReadOnlyList<string> MapToSdkToolNames(IEnumerable<string> tools) => tools
		.SelectMany(tool => tool switch
		{
			"Bash" => new[] { "bash", "write_bash", "powershell", "write_powershell" },
			"Edit" or "Write" or "MultiEdit" or "NotebookEdit" => new[] { "edit", "create", "apply_patch", "str_replace_editor" },
			_ => new[] { tool }
		})
		.Distinct(StringComparer.Ordinal)
		.ToList();

	/// <summary>
	/// What a job run needs on a new or resumed session alike: the job's rules, its reasoning
	/// effort, the tools its stage may not use, and its checkout.
	/// </summary>
	internal void ApplyRunSettings(SessionConfigBase config, string workingDirectory)
	{
		config.Model = ResolveModel();
		config.Streaming = true;
		config.WorkingDirectory = workingDirectory;
		ApplyByokConfig(config);

		if (!string.IsNullOrEmpty(CurrentReasoningEffort))
		{
			config.ReasoningEffort = CurrentReasoningEffort;
		}

		if (!string.IsNullOrWhiteSpace(CurrentSystemPrompt))
		{
			config.SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = CurrentSystemPrompt };
		}
		else if (!string.IsNullOrWhiteSpace(CurrentAppendSystemPrompt))
		{
			config.SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = CurrentAppendSystemPrompt };
		}

		if (CurrentDisallowedTools is { Count: > 0 } disallowed)
		{
			config.ExcludedTools = MapToSdkToolNames(disallowed).ToList();
		}
	}

	/// <summary>
	/// Applies BYOK (Bring Your Own Key) provider configuration to a session config
	/// when an API endpoint is configured. This enables routing requests through
	/// custom providers like OpenAI, Azure AI Foundry, or Anthropic.
	/// </summary>
	private void ApplyByokConfig(SessionConfigBase sessionConfig)
	{
		if (!string.IsNullOrEmpty(ApiEndpoint))
		{
			sessionConfig.Provider = new ProviderConfig
			{
				Type = InferProviderType(ApiEndpoint),
				BaseUrl = ApiEndpoint,
				ApiKey = ApiKey ?? string.Empty
			};
		}
	}

	private static string? InferProviderType(string endpoint)
	{
		if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
		{
			return null;
		}

		var host = uri.Host.ToLowerInvariant();
		if (host.Contains("openai.azure"))
		{
			return "azure";
		}

		if (host.Contains("openai"))
		{
			return "openai";
		}

		if (host.Contains("anthropic"))
		{
			return "anthropic";
		}

		return null;
	}

	public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
	{
		// Validate CLI path before attempting connection
		if (!string.IsNullOrEmpty(ExecutablePath))
		{
			var resolvedPath = Path.IsPathRooted(ExecutablePath)
				? ExecutablePath
				: Path.GetFullPath(ExecutablePath);

			if (!File.Exists(resolvedPath))
			{
				IsConnected = false;
				LastConnectionError = $"Copilot CLI not found at path: {resolvedPath}. Please verify the executable path or leave it empty to use PATH.";
				return false;
			}
		}

		// Retry once with a fresh client if the first attempt fails (handles stale/crashed CLI processes)
		for (var attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				var client = await EnsureClientAsync(cancellationToken: cancellationToken);
				var ping = await client.PingAsync(cancellationToken: cancellationToken);

				IsConnected = ping != null;
				LastConnectionError = IsConnected ? null : "Copilot SDK ping returned null.";
				return IsConnected;
			}
			catch (Exception) when (attempt == 0)
			{
				// First attempt failed — reset the client and try once more with a fresh process
				await ResetClientAsync();
			}
			catch (Exception ex)
			{
				IsConnected = false;
				await ResetClientAsync();

				var cliPath = ExecutablePath ?? "copilot (from PATH)";

				// Provide specific hints based on the error
				var hint = ex.Message.Contains("JSON-RPC", StringComparison.OrdinalIgnoreCase) ||
						   ex.Message.Contains("RPC", StringComparison.OrdinalIgnoreCase)
					? " The CLI may not support SDK mode or version is incompatible. Verify 'copilot --version' shows 1.0.0+ and GitHub.Copilot.SDK is compatible."
					: ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
					  ex.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
					? " CLI executable not found in PATH. Install the GitHub Copilot CLI or specify the full path."
					: ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase)
					? " Authentication may have expired. Run 'copilot login' to authenticate."
					: string.Empty;

				LastConnectionError = $"Failed to connect to Copilot SDK (CLI: {cliPath}): {ex.Message}{hint}";
				return false;
			}
		}

		return false;
	}

	public override async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
	{
		try
		{
			var client = await EnsureClientAsync(cancellationToken: cancellationToken);

			var sessionConfig = new SessionConfig { Model = ResolveModel() };
			ApplyByokConfig(sessionConfig);
			ApplySessionDefaults(sessionConfig);
			await using var session = await client.CreateSessionAsync(sessionConfig);

			var responseBuilder = new StringBuilder();
			var done = new TaskCompletionSource();

			using var _ = session.On<SessionEvent>(evt =>
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
			cts.CancelAfter(DefaultPromptTimeout);
			using var reg = cts.Token.Register(() => done.TrySetCanceled());

			await done.Task;

			return responseBuilder.ToString();
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			// Reset the client so the next call starts a fresh CLI process
			await ResetClientAsync();
			throw;
		}
	}

	public override async Task<ExecutionResult> ExecuteWithOptionsAsync(
		string prompt,
		ExecutionOptions options,
		IProgress<ExecutionProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		await ResetClientAsync();
		ApplyOptions(options);

		try
		{
			return await ExecuteWithSessionAsync(
				prompt,
				options.SessionId,
				options.WorkingDirectory,
				progress,
				cancellationToken);
		}
		finally
		{
			ClearExecutionContext();
			await ResetClientAsync();
		}
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

			result.CommandUsed = $"Copilot SDK ({model ?? "default model"})";

			var sessionConfig = new SessionConfig
			{
				InfiniteSessions = new InfiniteSessionConfig { Enabled = true }
			};
			ApplySessionDefaults(sessionConfig);
			ApplyRunSettings(sessionConfig, effectiveWorkingDir);

			if (!string.IsNullOrEmpty(sessionId))
			{
				sessionConfig.SessionId = sessionId;
			}

			// Create or resume session
			CopilotSession session;
			if (!string.IsNullOrEmpty(sessionId))
			{
				try
				{
					var resumeConfig = new ResumeSessionConfig();
					ApplyResumeSessionDefaults(resumeConfig);
					ApplyRunSettings(resumeConfig, effectiveWorkingDir);
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
					IsStreaming = false,
					SessionId = session.SessionId
				});

				// Subscribe to all session events
				using var subscription = session.On<SessionEvent>(evt =>
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

				// Send the prompt with optional file attachments
				var messageOptions = new MessageOptions { Prompt = prompt };

				if (CurrentAttachedFiles is { Count: > 0 })
				{
					messageOptions.Attachments = CurrentAttachedFiles
						.Select(f => (Attachment)new AttachmentFile
						{
							Path = f,
							DisplayName = System.IO.Path.GetFileName(f)
						})
						.ToList();
				}

				await session.SendAsync(messageOptions);

				// Wait for session idle or cancellation. No time cap of its own: like a CLI run, a
				// job ends when it finishes, is cancelled, or the watchdog finds it stalled.
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested && _retryCount < MaxRetries)
		{
			_retryCount++;
			progress?.Report(new ExecutionProgress
			{
				OutputLine = $"[Retry] Transient error (attempt {_retryCount}/{MaxRetries}): {ex.Message}",
				IsErrorOutput = true,
				IsStreaming = false
			});

			await ResetClientAsync();

			// Retry the execution
			return await ExecuteWithSessionAsync(prompt, sessionId, workingDirectory, progress, cancellationToken);
		}
		catch (Exception ex)
		{
			result.Success = false;
			result.ErrorMessage = $"Copilot SDK error: {ex.Message}";
			// Reset the client so the next execution starts a fresh CLI process
			await ResetClientAsync();
		}
		finally
		{
			_retryCount = 0;
		}

		return result;
	}

	private const int MaxRetries = 2;
	private int _retryCount;

	/// <summary>
	/// Processes a typed SDK session event, populating the result
	/// and streaming progress to the UI.
	/// </summary>
	private void ProcessSessionEvent(
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
#pragma warning disable GHCP001 // Cost is marked experimental in the SDK
					if (usage.Data.Cost.HasValue)
						result.CostUsd = (result.CostUsd ?? 0) + (decimal)usage.Data.Cost.Value;
#pragma warning restore GHCP001

					progress?.Report(new ExecutionProgress
					{
						TokensUsed = (result.InputTokens ?? 0) + (result.OutputTokens ?? 0),
						IsStreaming = false
					});
					break;
				}

			case SessionShutdownEvent shutdown:
				{
					// The session-wide total went internal in SDK 1.x; the per-model request cost is the
					// same premium-request figure the CLI reports.
#pragma warning disable GHCP001 // Requests.Cost is marked experimental in the SDK
					var premiumRequests = shutdown.Data.ModelMetrics?.Values.Sum(metric => metric.Requests?.Cost ?? 0) ?? 0;
#pragma warning restore GHCP001
					if (premiumRequests > 0)
					{
						result.PremiumRequestsConsumed = (int)Math.Round(premiumRequests);
						_lastObservedUsageLimits = new UsageLimits
						{
							LimitType = UsageLimitType.PremiumRequests,
							Message = $"Latest session consumed {premiumRequests:0.##} premium requests"
						};
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
							IsStreaming = true,
							IsThinkingContent = true,
							ContentCategory = "reasoning"
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
			AvailableModels = GetFallbackAvailableModels().ToList(),
			AvailableAgents = new List<AgentInfo>
			{
				new() { Name = "default", Description = "GitHub Copilot SDK agent", IsDefault = true }
			},
			Pricing = new PricingInfo
			{
				Currency = "USD"
			},
			ModelRetirementDates = new Dictionary<string, DateTime>(UpstreamRetirementDates),
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
			await ResetClientAsync();
		}

		return info;
	}

	public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
	{
		if (_lastObservedUsageLimits != null)
		{
			return Task.FromResult(_lastObservedUsageLimits);
		}

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
				ApplyResumeSessionDefaults(config);
				var session = await _client.ResumeSessionAsync(sessionId, config, cancellationToken);
				await using (session)
				{
					var messages = await session.GetEventsAsync();
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
			ApplyByokConfig(sessionConfig);
			ApplySessionDefaults(sessionConfig);
			await using var session = await client.CreateSessionAsync(sessionConfig);

			var responseBuilder = new StringBuilder();
			var done = new TaskCompletionSource();

			using var subscription = session.On<SessionEvent>(evt =>
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
			await ResetClientAsync();
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
