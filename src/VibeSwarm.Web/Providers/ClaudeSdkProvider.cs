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
	private const string DefaultModel = "claude-sonnet-4-5-20250929";

	private static readonly string[] AvailableModels =
	[
		"claude-sonnet-4-5-20250929",
		"claude-opus-4-20250514",
		"claude-haiku-4-5-20251001"
	];

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
			"sonnet" => "claude-sonnet-4-5-20250929",
			"opus" => "claude-opus-4-20250514",
			"haiku" => "claude-haiku-4-5-20251001",
			_ => model
		};
	}

	public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var client = EnsureClient();

			// Minimal call to verify API key works
			var message = await client.Messages.Create(new MessageCreateParams
			{
				MaxTokens = 1,
				Messages = [new MessageParam { Role = Role.User, Content = "Hi" }],
				Model = DefaultModel
			}, cancellationToken);

			IsConnected = true;
			LastConnectionError = null;
			return true;
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
			MaxTokens = 8192,
			Messages = [new MessageParam { Role = Role.User, Content = prompt }],
			Model = model
		}, cancellationToken);

		return ExtractTextContent(message);
	}

	public override async Task<ExecutionResult> ExecuteWithSessionAsync(
		string prompt,
		string? sessionId = null,
		string? workingDirectory = null,
		IProgress<ExecutionProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		var client = EnsureClient();
		var model = ResolveModel(CurrentModel);
		var result = new ExecutionResult { Messages = new List<ExecutionMessage>() };

		var messages = new List<MessageParam>
		{
			new() { Role = Role.User, Content = prompt }
		};

		var createParams = new MessageCreateParams
		{
			MaxTokens = 8192,
			Messages = messages,
			Model = model
		};

		try
		{
			progress?.Report(new ExecutionProgress
			{
				CurrentMessage = "Connecting to Claude SDK...",
				IsStreaming = false
			});

			var contentBuilder = new System.Text.StringBuilder();
			long inputTokens = 0;
			long outputTokens = 0;
			string? modelUsed = null;
			string? messageId = null;

			await foreach (var evt in client.Messages.CreateStreaming(createParams).WithCancellation(cancellationToken))
			{
				if (evt.TryPickStart(out var startEvent))
				{
					messageId = startEvent.Message.ID;
					modelUsed = startEvent.Message.Model.ToString();
					if (startEvent.Message.Usage != null)
					{
						inputTokens = startEvent.Message.Usage.InputTokens;
					}

					progress?.Report(new ExecutionProgress
					{
						CurrentMessage = "Processing...",
						IsStreaming = true
					});
				}
				else if (evt.TryPickContentBlockDelta(out var deltaEvent))
				{
					var deltaText = deltaEvent.Delta.ToString();
					if (!string.IsNullOrEmpty(deltaText))
					{
						contentBuilder.Append(deltaText);

						progress?.Report(new ExecutionProgress
						{
							OutputLine = deltaText,
							IsStreaming = true
						});
					}
				}
				else if (evt.TryPickDelta(out var messageDelta))
				{
					if (messageDelta.Usage != null)
					{
						outputTokens = messageDelta.Usage.OutputTokens;
					}
				}
			}

			var content = contentBuilder.ToString();

			result.Success = true;
			result.SessionId = messageId ?? sessionId;
			result.Output = content;
			result.ModelUsed = modelUsed;
			result.InputTokens = (int)inputTokens;
			result.OutputTokens = (int)outputTokens;
			result.CommandUsed = $"Claude SDK ({model})";

			if (!string.IsNullOrEmpty(content))
			{
				result.Messages.Add(new ExecutionMessage
				{
					Role = "assistant",
					Content = content,
					Timestamp = DateTime.UtcNow
				});
			}
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

		return result;
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
				InputTokenPricePerMillion = 3.00m,
				OutputTokenPricePerMillion = 15.00m,
				Currency = "USD",
				ModelMultipliers = new Dictionary<string, decimal>
				{
					["claude-sonnet-4-5-20250929"] = 1.0m,
					["claude-opus-4-20250514"] = 5.0m,
					["claude-haiku-4-5-20251001"] = 0.27m
				}
			},
			AdditionalInfo = new Dictionary<string, object>
			{
				["isAvailable"] = true,
				["connectionMode"] = "SDK"
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

	public override ValueTask DisposeAsync()
	{
		// AnthropicClient doesn't implement IDisposable, but we clear our reference
		_client = null;
		GC.SuppressFinalize(this);
		return ValueTask.CompletedTask;
	}
}
