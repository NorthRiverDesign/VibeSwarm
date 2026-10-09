using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Claude provider using the official Anthropic C# SDK (NuGet: Anthropic).
/// Provides typed API access, structured streaming, built-in retries,
/// and proper error handling without CLI process spawning.
/// </summary>
public class ClaudeSdkProvider : SdkProviderBase
{
	private AnthropicClient? _client;
	private const string DefaultModel = "claude-opus-5-5";
	private const string ConnectionTestFallbackModel = "claude-haiku-4-5";
	private const int MaxTokensPerTurn = 16000;
	private const int DefaultMaxTurns = 200;
	private UsageLimits? _lastObservedUsageLimits;

	private static readonly string[] AvailableModels =
	[
		"claude-opus-5-5",
		"claude-sonnet-5-5",
		"claude-fable-5-1",
		"claude-haiku-4-5"
	];

	/// <summary>
	/// Tells the model how this harness works. The job's own rules arrive through the appended
	/// system prompt, as they do for the CLI.
	/// </summary>
	private const string AgentSystemPrompt =
		"You are a coding agent working in a git checkout. Use the bash tool to run commands and the " +
		"str_replace_based_edit_tool to view and edit files. Each bash command runs in a fresh shell " +
		"in the repository root, so chain commands that depend on each other (cd dir && make). " +
		"Paths are relative to the repository root. Keep working until the task is done, then reply " +
		"without calling a tool.";

	public override ProviderType Type => ProviderType.Claude;

	public ClaudeSdkProvider(Provider config) : base(config) { }

	/// <summary>
	/// Lazily initializes the Anthropic SDK client.
	/// </summary>
	private AnthropicClient EnsureClient()
	{
		if (_client != null) return _client;

		var apiKey = ApiKey ?? throw new InvalidOperationException("API Key is required for Claude SDK mode.");

		_client = new AnthropicClient
		{
			ApiKey = apiKey,
			MaxRetries = 2,
			Timeout = TimeSpan.FromMinutes(30),
			BaseUrl = !string.IsNullOrEmpty(ApiEndpoint) ? ApiEndpoint : "https://api.anthropic.com"
		};

		return _client;
	}

	/// <summary>
	/// Resolves the model string to use. Accepts full model IDs or short aliases.
	/// </summary>
	private static string ResolveModel(string? model)
	{
		if (string.IsNullOrEmpty(model)) return DefaultModel;

		// Strip provider prefix (e.g., "anthropic/claude-sonnet-4-5-20250929")
		if (model.Contains('/'))
		{
			model = model[(model.LastIndexOf('/') + 1)..];
		}

		// Map short aliases
		return model.ToLowerInvariant() switch
		{
			"sonnet" => "claude-sonnet-5-5",
			"opus" => "claude-opus-5-5",
			"haiku" => "claude-haiku-4-5",
			"fable" => "claude-fable-5-1",
			_ => model
		};
	}

	/// <summary>
	/// Haiku 4.5 predates adaptive thinking and the effort setting; every other current model takes both.
	/// </summary>
	private static bool SupportsAdaptiveThinking(string model) =>
		!model.StartsWith("claude-haiku-4", StringComparison.OrdinalIgnoreCase);

	public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
	{
		var attemptedModels = new List<string>();

		try
		{
			var client = EnsureClient();

			foreach (var model in GetConnectionTestModels())
			{
				attemptedModels.Add(model);

				try
				{
					await client.Messages.Create(new MessageCreateParams
					{
						MaxTokens = 1,
						Messages = [new MessageParam { Role = Role.User, Content = "Hi" }],
						Model = model
					}, cancellationToken);

					IsConnected = true;
					LastConnectionError = null;
					return true;
				}
				catch (AnthropicUnauthorizedException)
				{
					throw;
				}
				catch (AnthropicRateLimitException)
				{
					IsConnected = true;
					LastConnectionError = null;
					return true;
				}
				catch (AnthropicApiException ex) when (IsModelNotFoundError(ex))
				{
					continue;
				}
			}

			IsConnected = false;
			LastConnectionError = $"Claude API rejected all fallback test models. Tried: {string.Join(", ", attemptedModels)}. Refresh models or choose a different SDK model.";
			return false;
		}
		catch (AnthropicUnauthorizedException)
		{
			IsConnected = false;
			LastConnectionError = "Invalid API key. Please check your Anthropic API key.";
			return false;
		}
		catch (AnthropicRateLimitException)
		{
			// Rate limited but key is valid
			IsConnected = true;
			LastConnectionError = null;
			return true;
		}
		catch (AnthropicApiException ex)
		{
			IsConnected = false;
			LastConnectionError = $"Claude API error: {ex.Message}";
			return false;
		}
		catch (Exception ex)
		{
			IsConnected = false;
			LastConnectionError = $"Failed to connect to Claude SDK: {ex.Message}";
			return false;
		}
	}

	public override async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
	{
		var client = EnsureClient();
		var model = ResolveModel(CurrentModel);

		var message = await client.Messages.Create(new MessageCreateParams
		{
			MaxTokens = MaxTokensPerTurn,
			Messages = [new MessageParam { Role = Role.User, Content = prompt }],
			Model = model
		}, cancellationToken);

		EnsureCompleted(message);
		return ExtractTextContent(message);
	}

	/// <summary>
	/// Runs the job as an agent: the model works through the bash and text editor tools in the
	/// checkout until it replies without calling one. A run that stops for any other reason
	/// (output limit, refusal, too many turns) is a failure, never a success.
	/// </summary>
	public override async Task<ExecutionResult> ExecuteWithSessionAsync(
		string prompt,
		string? sessionId = null,
		string? workingDirectory = null,
		IProgress<ExecutionProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		var client = EnsureClient();
		var model = ResolveModel(CurrentModel);
		var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;
		var result = new ExecutionResult
		{
			Messages = new List<ExecutionMessage>(),
			SessionId = sessionId ?? Guid.NewGuid().ToString(),
			CommandUsed = $"Claude SDK ({model})",
			ModelUsed = model
		};

		var disallowed = CurrentDisallowedTools ?? [];
		var allowShell = !disallowed.Contains("Bash", StringComparer.Ordinal);
		var tools = new ClaudeSdkToolExecutor(effectiveWorkingDir, CurrentEnvironmentVariables)
		{
			AllowEdits = !disallowed.Any(tool => tool is "Edit" or "Write" or "MultiEdit")
		};

		List<ToolUnion> toolDefinitions = [new ToolUnion(new ToolTextEditor20250728())];
		if (allowShell)
		{
			toolDefinitions.Insert(0, new ToolUnion(new ToolBash20250124()));
		}

		var systemPrompt = string.IsNullOrWhiteSpace(CurrentSystemPrompt) ? AgentSystemPrompt : CurrentSystemPrompt;
		if (!string.IsNullOrWhiteSpace(CurrentAppendSystemPrompt))
		{
			systemPrompt += "\n\n" + CurrentAppendSystemPrompt;
		}

		var messages = new List<MessageParam> { new() { Role = Role.User, Content = prompt } };
		var finalText = new System.Text.StringBuilder();
		long inputTokens = 0;
		long outputTokens = 0;
		var maxTurns = CurrentMaxTurns ?? DefaultMaxTurns;

		try
		{
			progress?.Report(new ExecutionProgress { CurrentMessage = "Starting Claude SDK agent...", SessionId = result.SessionId });

			for (var turn = 1; ; turn++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (turn > maxTurns)
				{
					result.Success = false;
					result.ErrorMessage = $"The agent did not finish within {maxTurns} turns.";
					break;
				}

				var request = new MessageCreateParams
				{
					Model = model,
					MaxTokens = MaxTokensPerTurn,
					System = systemPrompt,
					Messages = messages,
					Tools = toolDefinitions,
					// Every turn resends the conversation; caching it keeps a long job affordable.
					CacheControl = new CacheControlEphemeral()
				};
				if (SupportsAdaptiveThinking(model))
				{
					request = request with { Thinking = new ThinkingConfigAdaptive() };
					if (ParseEffort(CurrentReasoningEffort) is { } effort)
					{
						request = request with { OutputConfig = new OutputConfig { Effort = effort } };
					}
				}

				var response = await client.Messages.Create(request, cancellationToken);
				inputTokens += response.Usage.InputTokens
					+ (response.Usage.CacheReadInputTokens ?? 0)
					+ (response.Usage.CacheCreationInputTokens ?? 0);
				outputTokens += response.Usage.OutputTokens;

				var assistantContent = new List<ContentBlockParam>();
				var toolResults = new List<ContentBlockParam>();
				var turnText = new System.Text.StringBuilder();
				foreach (var block in response.Content)
				{
					if (block.TryPickText(out var text))
					{
						assistantContent.Add(new TextBlockParam { Text = text.Text });
						turnText.Append(text.Text);
					}
					else if (block.TryPickThinking(out var thinking))
					{
						assistantContent.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
					}
					else if (block.TryPickRedactedThinking(out var redacted))
					{
						assistantContent.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
					}
					else if (block.TryPickToolUse(out var toolUse))
					{
						assistantContent.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
					}
				}

				if (turnText.Length > 0)
				{
					result.Messages.Add(new ExecutionMessage { Role = "assistant", Content = turnText.ToString(), Timestamp = DateTime.UtcNow });
					progress?.Report(new ExecutionProgress { OutputLine = turnText.ToString(), ContentCategory = "text" });
				}

				var stopReason = StopReasonOf(response);
				if (stopReason != "tool_use")
				{
					finalText.Append(turnText);
					if (stopReason is "end_turn" or "stop_sequence")
					{
						result.Success = true;
					}
					else
					{
						result.Success = false;
						result.ErrorMessage = DescribeStop(response);
					}
					break;
				}

				foreach (var block in response.Content)
				{
					if (!block.TryPickToolUse(out var toolUse))
					{
						continue;
					}

					var input = System.Text.Json.JsonSerializer.SerializeToElement(toolUse.Input);
					progress?.Report(new ExecutionProgress { CurrentMessage = $"Running {toolUse.Name}", ToolName = toolUse.Name });
					var (output, isError) = await tools.RunAsync(toolUse.Name, input, cancellationToken);
					toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = output, IsError = isError });
					result.Messages.Add(new ExecutionMessage
					{
						Role = "tool",
						Content = output,
						ToolName = toolUse.Name,
						ToolInput = input.GetRawText(),
						ToolOutput = output,
						Timestamp = DateTime.UtcNow
					});
				}

				messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
				messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
			}

			result.Output = finalText.ToString();
		}
		catch (AnthropicRateLimitException ex)
		{
			result.Success = false;
			result.ErrorMessage = $"Rate limit exceeded: {ex.Message}";
			result.DetectedUsageLimits = new UsageLimits
			{
				LimitType = UsageLimitType.RateLimit,
				IsLimitReached = true,
				Message = ex.Message
			};
			_lastObservedUsageLimits = result.DetectedUsageLimits;
		}
		catch (AnthropicApiException ex)
		{
			result.Success = false;
			result.ErrorMessage = $"Claude API error: {ex.Message}";
		}
		catch (OperationCanceledException)
		{
			result.Success = false;
			result.ErrorMessage = "Execution was cancelled.";
		}
		catch (Exception ex)
		{
			result.Success = false;
			result.ErrorMessage = $"Unexpected error: {ex.Message}";
		}

		result.InputTokens = (int)Math.Min(int.MaxValue, inputTokens);
		result.OutputTokens = (int)Math.Min(int.MaxValue, outputTokens);
		return result;
	}

	/// <summary>The wire value, such as end_turn or tool_use.</summary>
	internal static string? StopReasonOf(Message message) => message.StopReason?.Raw();

	private static Effort? ParseEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
	{
		"low" => Effort.Low,
		"medium" or "standard" => Effort.Medium,
		"high" => Effort.High,
		"xhigh" => Effort.Xhigh,
		"max" => Effort.Max,
		_ => null
	};

	private static string DescribeStop(Message response)
	{
		var stopReason = StopReasonOf(response);
		return stopReason switch
		{
			"max_tokens" => "The model hit its output limit before finishing.",
			"refusal" => $"The model declined the request{(response.StopDetails is { } details ? $" ({details.Category}): {details.Explanation}" : ".")}",
			_ => $"The model stopped unexpectedly ({stopReason ?? "no reason given"})."
		};
	}

	/// <summary>
	/// A reply cut off by the output limit, or refused, is not an answer.
	/// </summary>
	private static void EnsureCompleted(Message message)
	{
		var stopReason = StopReasonOf(message);
		if (stopReason is not ("end_turn" or "stop_sequence"))
		{
			throw new InvalidOperationException(DescribeStop(message));
		}
	}

	public override async Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default)
	{
		var info = new ProviderInfo
		{
			Version = "SDK (Anthropic NuGet)",
			AvailableModels = new List<string>(AvailableModels),
			AvailableAgents = new List<AgentInfo>
			{
				new() { Name = "default", Description = "Claude SDK with direct API access", IsDefault = true }
			},
			Pricing = new PricingInfo
			{
				// Claude Sonnet 5.5 rates; the other models are multiples of them.
				InputTokenPricePerMillion = 2.00m,
				OutputTokenPricePerMillion = 10.00m,
				Currency = "USD",
				ModelMultipliers = new Dictionary<string, decimal>
				{
					["claude-opus-5-5"] = 2.0m,
					["claude-sonnet-5-5"] = 1.0m,
					["claude-fable-5-1"] = 5.0m,
					["claude-haiku-4-5"] = 0.5m
				}
			},
			ModelRetirementDates = new Dictionary<string, DateTime>
			{
				["claude-haiku-3"] = new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc),
				["claude-3-haiku-20240307"] = new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc),
				["claude-sonnet-4-20250514"] = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
				["claude-opus-4-20250514"] = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
			},
			AdditionalInfo = new Dictionary<string, object>
			{
				["isAvailable"] = true,
				["connectionMode"] = "SDK",
				["modelsWarning"] = "Claude SDK currently uses a curated fallback model list. Account access can vary by Anthropic workspace, so connection testing tries multiple fallback models."
			}
		};

		// Try to verify connection for availability
		try
		{
			var connected = await TestConnectionAsync(cancellationToken);
			info.AdditionalInfo["isAvailable"] = connected;
		}
		catch
		{
			info.AdditionalInfo["isAvailable"] = false;
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
			LimitType = UsageLimitType.RateLimit,
			IsLimitReached = false,
			Message = "Rate limits managed by Anthropic API (auto-retry enabled via SDK)"
		});
	}

	public override Task<SessionSummary> GetSessionSummaryAsync(
		string? sessionId,
		string? workingDirectory = null,
		string? fallbackOutput = null,
		CancellationToken cancellationToken = default)
	{
		var summary = new SessionSummary();

		if (!string.IsNullOrEmpty(fallbackOutput))
		{
			summary.Summary = OutputSummaryHelper.GenerateSummaryFromOutput(fallbackOutput);
			summary.Success = !string.IsNullOrEmpty(summary.Summary);
			summary.Source = "output";
			return Task.FromResult(summary);
		}

		summary.Success = false;
		summary.ErrorMessage = "SDK mode does not support session resumption for summaries. Provide fallback output.";
		return Task.FromResult(summary);
	}

	public override async Task<PromptResponse> GetPromptResponseAsync(
		string prompt,
		string? workingDirectory = null,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		try
		{
			var client = EnsureClient();
			var model = ResolveModel(CurrentModel);

			var message = await client.Messages.Create(new MessageCreateParams
			{
				MaxTokens = 4096,
				Messages = [new MessageParam { Role = Role.User, Content = prompt }],
				Model = model
			}, cancellationToken);

			stopwatch.Stop();

			var text = ExtractTextContent(message);
			return PromptResponse.Ok(text, stopwatch.ElapsedMilliseconds, message.Model.ToString());
		}
		catch (AnthropicRateLimitException ex)
		{
			return PromptResponse.Fail($"Rate limit exceeded: {ex.Message}");
		}
		catch (AnthropicApiException ex)
		{
			return PromptResponse.Fail($"Claude API error: {ex.Message}");
		}
		catch (OperationCanceledException)
		{
			return PromptResponse.Fail("Request was cancelled.");
		}
		catch (Exception ex)
		{
			return PromptResponse.Fail($"Error calling Claude SDK: {ex.Message}");
		}
	}

	/// <summary>
	/// Extracts text content from a Claude API response message.
	/// </summary>
	private static string ExtractTextContent(Message message)
	{
		var texts = new List<string>();
		foreach (var block in message.Content)
		{
			if (block.TryPickText(out var textBlock))
			{
				texts.Add(textBlock.Text);
			}
		}
		return string.Join("", texts);
	}

	private static IEnumerable<string> GetConnectionTestModels()
	{
		var models = new[]
		{
			ConnectionTestFallbackModel,
			DefaultModel,
			"claude-sonnet-5-5"
		};

		return models.Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static bool IsModelNotFoundError(AnthropicApiException ex)
		=> ex.Message.Contains("not_found_error", StringComparison.OrdinalIgnoreCase)
			|| ex.Message.Contains("model:", StringComparison.OrdinalIgnoreCase)
			|| ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

	public override ValueTask DisposeAsync()
	{
		// AnthropicClient doesn't implement IDisposable, but we clear our reference
		_client = null;
		GC.SuppressFinalize(this);
		return ValueTask.CompletedTask;
	}
}
