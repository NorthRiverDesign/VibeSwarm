using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

public class CopilotProvider : ProviderBase
{
	private readonly string? _executablePath;
	private readonly string? _workingDirectory;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
	};

	public override ProviderType Type => ProviderType.Copilot;

	public CopilotProvider(Provider config)
		: base(config.Id, config.Name, ProviderConnectionMode.CLI)
	{
		_executablePath = config.ExecutablePath;
		_workingDirectory = config.WorkingDirectory;
		// Copilot CLI only supports CLI mode (no REST API)
		ConnectionMode = ProviderConnectionMode.CLI;
	}

	public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			return await TestCliConnectionAsync(cancellationToken);
		}
		catch
		{
			IsConnected = false;
			return false;
		}
	}

	private async Task<bool> TestCliConnectionAsync(CancellationToken cancellationToken)
	{
		var execPath = GetExecutablePath();
		if (string.IsNullOrEmpty(execPath))
		{
			IsConnected = false;
			LastConnectionError = "GitHub Copilot CLI executable path is not configured.";
			return false;
		}

		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = execPath,
				Arguments = "--version"
			};

			// Configure for cross-platform with enhanced PATH (essential for systemd services)
			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			// Only set working directory if explicitly configured
			if (!string.IsNullOrEmpty(_workingDirectory))
			{
				startInfo.WorkingDirectory = _workingDirectory;
			}

			using var process = new Process { StartInfo = startInfo };

			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			process.Start();

			try
			{
				process.StandardInput.Close();
			}
			catch
			{
				// Ignore if stdin is already closed
			}

			try
			{
				await process.WaitForExitAsync(linkedCts.Token);
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				IsConnected = false;
				LastConnectionError = $"CLI test timed out after 10 seconds. Command: {execPath} --version\n" +
					$"Working directory: {_workingDirectory ?? Environment.CurrentDirectory}\n" +
					"This usually indicates:\n" +
					"  - The CLI is waiting for authentication (run 'gh auth login' first)\n" +
					"  - The CLI is trying to access the network and timing out\n" +
					"  - GitHub Copilot is not enabled for your account";
				return false;
			}

			var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
			var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);

			IsConnected = process.ExitCode == 0 && !string.IsNullOrEmpty(output);

			if (!IsConnected)
			{
				var errorDetails = new System.Text.StringBuilder();
				errorDetails.AppendLine($"CLI test failed for command: {execPath} --version");
				errorDetails.AppendLine($"Exit code: {process.ExitCode}");
				errorDetails.AppendLine($"Working directory: {_workingDirectory ?? Environment.CurrentDirectory}");

				if (!string.IsNullOrEmpty(error))
				{
					errorDetails.AppendLine($"Error output: {error.Trim()}");
				}

				if (string.IsNullOrEmpty(output))
				{
					errorDetails.AppendLine("No output received from --version command.");
				}
				else
				{
					errorDetails.AppendLine($"Output: {output.Trim()}");
				}

				LastConnectionError = errorDetails.ToString();
			}
			else
			{
				LastConnectionError = null;
			}

			return IsConnected;
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			IsConnected = false;
			var envPath = Environment.GetEnvironmentVariable("PATH") ?? "not set";
			LastConnectionError = $"Failed to start GitHub Copilot CLI: {ex.Message}. " +
				$"Executable path: '{execPath}'. " +
				$"Current PATH: {envPath}. " +
				$"If running as a systemd service, ensure the executable is in a standard location " +
				$"or configure the full path to the executable in the provider settings.";
			return false;
		}
		catch (Exception ex)
		{
			IsConnected = false;
			LastConnectionError = $"Unexpected error testing GitHub Copilot CLI connection: {ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}

	public override async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
	{
		return await ExecuteCliAsync(prompt, cancellationToken);
	}

	public override async Task<ExecutionResult> ExecuteWithSessionAsync(
		string prompt,
		string? sessionId = null,
		string? workingDirectory = null,
		IProgress<ExecutionProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		return await ExecuteCliWithSessionAsync(prompt, sessionId, workingDirectory, progress, cancellationToken);
	}

	private async Task<ExecutionResult> ExecuteCliWithSessionAsync(
		string prompt,
		string? sessionId,
		string? workingDirectory,
		IProgress<ExecutionProgress>? progress,
		CancellationToken cancellationToken)
	{
		var execPath = GetExecutablePath();
		if (string.IsNullOrEmpty(execPath))
		{
			return new ExecutionResult
			{
				Success = false,
				ErrorMessage = "GitHub Copilot CLI executable path is not configured."
			};
		}

		var result = new ExecutionResult { Messages = new List<ExecutionMessage>() };
		var effectiveWorkingDir = workingDirectory ?? _workingDirectory ?? Environment.CurrentDirectory;

		// GitHub Copilot CLI (standalone 'copilot' command)
		// -p: non-interactive print mode (runs to completion without user input)
		// --allow-all-tools: allow all tools to run without confirmation (required for non-interactive)
		// -s/--silent: output only the agent response (useful for scripting)
		var args = new List<string>();

		// Add the prompt with -p flag for non-interactive mode
		args.Add("-p");
		args.Add($"\"{EscapeArgument(prompt)}\"");

		// Allow all tools to run without confirmation (required for non-interactive mode)
		args.Add("--allow-all-tools");

		// Add MCP config if available (injected via ExecuteWithOptionsAsync)
		// Copilot CLI uses --additional-mcp-config @filepath format
		if (!string.IsNullOrEmpty(CurrentMcpConfigPath))
		{
			args.Add("--additional-mcp-config");
			args.Add($"\"@{CurrentMcpConfigPath}\"");
		}

		// Add any additional CLI arguments
		if (CurrentAdditionalArgs != null)
		{
			args.AddRange(CurrentAdditionalArgs);
		}

		var startInfo = new ProcessStartInfo
		{
			FileName = execPath,
			Arguments = string.Join(" ", args),
			WorkingDirectory = effectiveWorkingDir,
		};

		// Use cross-platform configuration
		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		// Add any additional environment variables
		if (CurrentEnvironmentVariables != null)
		{
			foreach (var kvp in CurrentEnvironmentVariables)
			{
				startInfo.Environment[kvp.Key] = kvp.Value;
			}
		}

		using var process = new Process { StartInfo = startInfo };

		var outputBuilder = new List<string>();
		var errorBuilder = new System.Text.StringBuilder();
		var currentAssistantMessage = new System.Text.StringBuilder();

		var outputComplete = new TaskCompletionSource<bool>();
		var errorComplete = new TaskCompletionSource<bool>();

		try
		{
			process.Start();
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			return new ExecutionResult
			{
				Success = false,
				ErrorMessage = $"Failed to start GitHub Copilot CLI process: {ex.Message}. " +
					$"Ensure the executable at '{execPath}' is accessible and has execute permissions."
			};
		}
		catch (Exception ex)
		{
			return new ExecutionResult
			{
				Success = false,
				ErrorMessage = $"Failed to start process: {ex.Message}"
			};
		}

		result.ProcessId = process.Id;

		progress?.Report(new ExecutionProgress
		{
			CurrentMessage = "GitHub Copilot CLI process started",
			ProcessId = process.Id,
			IsStreaming = false
		});

		process.OutputDataReceived += (sender, e) =>
		{
			if (e.Data == null)
			{
				outputComplete.TrySetResult(true);
				return;
			}

			if (string.IsNullOrEmpty(e.Data)) return;

			outputBuilder.Add(e.Data);

			// Stream raw output line to UI
			progress?.Report(new ExecutionProgress
			{
				OutputLine = e.Data,
				IsErrorOutput = false
			});

			// Check for premium request limit messages
			if (e.Data.Contains("premium request", StringComparison.OrdinalIgnoreCase) ||
				e.Data.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
				e.Data.Contains("limit exceeded", StringComparison.OrdinalIgnoreCase))
			{
				progress?.Report(new ExecutionProgress
				{
					CurrentMessage = "Premium request limit detected",
					IsStreaming = false
				});
			}

			try
			{
				var jsonEvent = JsonSerializer.Deserialize<CopilotStreamEvent>(e.Data, JsonOptions);
				if (jsonEvent != null)
				{
					ProcessStreamEvent(jsonEvent, result, currentAssistantMessage, progress);
				}
			}
			catch
			{
				// Non-JSON output, append to current message
				currentAssistantMessage.Append(e.Data);
				currentAssistantMessage.AppendLine();
				progress?.Report(new ExecutionProgress
				{
					CurrentMessage = e.Data.Length > 100 ? e.Data[..100] + "..." : e.Data,
					IsStreaming = true
				});
			}
		};

		process.ErrorDataReceived += (sender, e) =>
		{
			if (e.Data == null)
			{
				errorComplete.TrySetResult(true);
				return;
			}

			if (!string.IsNullOrEmpty(e.Data))
			{
				errorBuilder.AppendLine(e.Data);

				// Stream error output line to UI
				progress?.Report(new ExecutionProgress
				{
					OutputLine = e.Data,
					IsErrorOutput = true
				});

				// Check for limit-related errors
				if (e.Data.Contains("premium", StringComparison.OrdinalIgnoreCase) ||
					e.Data.Contains("limit", StringComparison.OrdinalIgnoreCase))
				{
					result.ErrorMessage = e.Data;
				}
			}
		};

		process.StandardInput.Close();

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		// Wait for the process to exit - this can take several minutes for complex agent tasks
		try
		{
			await process.WaitForExitAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			// If cancelled, use cross-platform kill method
			PlatformHelper.TryKillProcessTree(process.Id);
			throw;
		}

		var outputTimeout = Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
		await Task.WhenAny(Task.WhenAll(outputComplete.Task, errorComplete.Task), outputTimeout);

		if (currentAssistantMessage.Length > 0)
		{
			result.Messages.Add(new ExecutionMessage
			{
				Role = "assistant",
				Content = currentAssistantMessage.ToString(),
				Timestamp = DateTime.UtcNow
			});
		}

		result.Success = process.ExitCode == 0;
		result.Output = string.Join("\n", outputBuilder);

		var error = errorBuilder.ToString();
		if (!result.Success && !string.IsNullOrEmpty(error))
		{
			result.ErrorMessage = error;
		}

		// Parse usage data from stderr (Copilot CLI outputs usage info to stderr)
		if (!string.IsNullOrEmpty(error))
		{
			ParseCopilotUsageFromStderr(error, result);
		}

		return result;
	}

	/// <summary>
	/// Parses usage information from Copilot CLI stderr output.
	/// Example format:
	/// [ERR]  claude-opus-4.5         49.0k in, 301 out, 32.0k cached (Est. 3 Premium requests)
	/// </summary>
	private static void ParseCopilotUsageFromStderr(string stderr, ExecutionResult result)
	{
		if (string.IsNullOrWhiteSpace(stderr))
			return;

		// Pattern to match: model_name  XXXk in, YYY out, ZZZk cached
		// Examples: "49.0k in, 301 out" or "1.2k in, 500 out, 32.0k cached"
		var usagePattern = new Regex(
			@"(\d+(?:\.\d+)?)\s*k?\s*in\s*,\s*(\d+(?:\.\d+)?)\s*k?\s*out",
			RegexOptions.IgnoreCase);

		var match = usagePattern.Match(stderr);
		if (match.Success)
		{
			// Parse input tokens
			if (double.TryParse(match.Groups[1].Value, out var inputValue))
			{
				// Check if it was in "k" format (the k is optional in the pattern)
				var inputStr = match.Groups[0].Value;
				if (inputStr.Contains("k in", StringComparison.OrdinalIgnoreCase))
				{
					result.InputTokens = (int)(inputValue * 1000);
				}
				else
				{
					result.InputTokens = (int)inputValue;
				}
			}

			// Parse output tokens
			if (double.TryParse(match.Groups[2].Value, out var outputValue))
			{
				var outputStr = match.Groups[0].Value;
				if (outputStr.Contains("k out", StringComparison.OrdinalIgnoreCase))
				{
					result.OutputTokens = (int)(outputValue * 1000);
				}
				else
				{
					result.OutputTokens = (int)outputValue;
				}
			}
		}

		// Also try to extract model name
		var modelPattern = new Regex(@"^\s*(?:\[ERR\])?\s*(claude-[\w.-]+|gpt-[\w.-]+|gemini-[\w.-]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
		var modelMatch = modelPattern.Match(stderr);
		if (modelMatch.Success && string.IsNullOrEmpty(result.ModelUsed))
		{
			result.ModelUsed = modelMatch.Groups[1].Value.Trim();
		}

		// Try to extract premium requests as a cost estimate
		var premiumPattern = new Regex(@"Est\.\s*(\d+)\s*Premium\s*requests?", RegexOptions.IgnoreCase);
		var premiumMatch = premiumPattern.Match(stderr);
		if (premiumMatch.Success && int.TryParse(premiumMatch.Groups[1].Value, out var premiumRequests))
		{
			// Store premium requests in cost field (as a count, not actual cost)
			// This gives users visibility into premium request usage
			if (!result.CostUsd.HasValue)
			{
				result.CostUsd = premiumRequests;
			}
		}
	}

	private void ProcessStreamEvent(
		CopilotStreamEvent evt,
		ExecutionResult result,
		System.Text.StringBuilder currentMessage,
		IProgress<ExecutionProgress>? progress)
	{
		switch (evt.Type?.ToLowerInvariant())
		{
			case "message":
			case "response":
			case "assistant":
				if (!string.IsNullOrEmpty(evt.Content))
				{
					currentMessage.Append(evt.Content);
					progress?.Report(new ExecutionProgress
					{
						CurrentMessage = evt.Content.Length > 100
							? evt.Content[..100] + "..."
							: evt.Content,
						IsStreaming = true
					});
				}
				break;

			case "suggestion":
				if (!string.IsNullOrEmpty(evt.Suggestion))
				{
					currentMessage.AppendLine(evt.Suggestion);
					progress?.Report(new ExecutionProgress
					{
						CurrentMessage = "Suggestion received",
						IsStreaming = false
					});
				}
				break;

			case "error":
				if (!string.IsNullOrEmpty(evt.Error))
				{
					result.ErrorMessage = evt.Error;
					progress?.Report(new ExecutionProgress
					{
						CurrentMessage = $"Error: {evt.Error}",
						IsStreaming = false
					});
				}
				break;

			case "limit":
			case "rate_limit":
				result.ErrorMessage = evt.Message ?? "Premium request limit reached";
				progress?.Report(new ExecutionProgress
				{
					CurrentMessage = "Premium request limit reached",
					IsStreaming = false
				});
				break;

			case "usage":
			case "metrics":
			case "stats":
				// Process token usage if provided
				if (evt.InputTokens.HasValue)
				{
					result.InputTokens = evt.InputTokens;
				}
				if (evt.OutputTokens.HasValue)
				{
					result.OutputTokens = evt.OutputTokens;
				}
				if (evt.CostUsd.HasValue)
				{
					result.CostUsd = evt.CostUsd;
				}
				break;

			case "done":
			case "complete":
			case "result":
				// Finalize - capture any token data in the completion event
				if (evt.InputTokens.HasValue)
				{
					result.InputTokens = evt.InputTokens;
				}
				if (evt.OutputTokens.HasValue)
				{
					result.OutputTokens = evt.OutputTokens;
				}
				if (evt.CostUsd.HasValue)
				{
					result.CostUsd = evt.CostUsd;
				}
				if (evt.Usage != null)
				{
					result.InputTokens = evt.Usage.InputTokens ?? result.InputTokens;
					result.OutputTokens = evt.Usage.OutputTokens ?? result.OutputTokens;
				}
				break;
		}
	}

	private async Task<string> ExecuteCliAsync(string prompt, CancellationToken cancellationToken)
	{
		var execPath = GetExecutablePath();
		if (string.IsNullOrEmpty(execPath))
		{
			throw new InvalidOperationException("GitHub Copilot CLI executable path is not configured.");
		}

		// Use -p for non-interactive mode and --allow-all-tools for automated execution
		var args = $"-p \"{EscapeArgument(prompt)}\" --allow-all-tools";

		var startInfo = new ProcessStartInfo
		{
			FileName = execPath,
			Arguments = args
		};

		// Configure for cross-platform with enhanced PATH
		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		// Only set working directory if explicitly configured
		if (!string.IsNullOrEmpty(_workingDirectory))
		{
			startInfo.WorkingDirectory = _workingDirectory;
		}

		using var process = new Process { StartInfo = startInfo };
		process.Start();

		var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
		var error = await process.StandardError.ReadToEndAsync(cancellationToken);

		await process.WaitForExitAsync(cancellationToken);

		if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
		{
			throw new InvalidOperationException($"GitHub Copilot execution failed: {error}");
		}

		return output;
	}

	public override async Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default)
	{
		var info = new ProviderInfo
		{
			// Models from `copilot help config`
			AvailableModels = new List<string>
			{
				"claude-sonnet-4.5",
				"claude-haiku-4.5",
				"claude-opus-4.5",
				"claude-sonnet-4",
				"gpt-5.2-codex",
				"gpt-5.1-codex-max",
				"gpt-5.1-codex",
				"gpt-5.2",
				"gpt-5.1",
				"gpt-5",
				"gpt-5.1-codex-mini",
				"gpt-5-mini",
				"gpt-4.1",
				"gemini-3-pro-preview"
			},
			AvailableAgents = new List<AgentInfo>
			{
				new() { Name = "suggest", Description = "Suggest shell commands, Git commands, or GitHub CLI commands", IsDefault = true },
				new() { Name = "explain", Description = "Explain code or commands", IsDefault = false }
			},
			Pricing = new PricingInfo
			{
				Currency = "USD",
				// Copilot pricing is subscription-based with premium request limits
				ModelMultipliers = new Dictionary<string, decimal>
				{
					["claude-sonnet-4.5"] = 1.0m,
					["claude-haiku-4.5"] = 0.2m,
					["claude-opus-4.5"] = 5.0m,
					["claude-sonnet-4"] = 1.0m,
					["gpt-5.2-codex"] = 1.5m,
					["gpt-5.1-codex-max"] = 2.0m,
					["gpt-5.1-codex"] = 1.0m,
					["gpt-5.2"] = 1.5m,
					["gpt-5.1"] = 1.0m,
					["gpt-5"] = 1.0m,
					["gpt-5.1-codex-mini"] = 0.3m,
					["gpt-5-mini"] = 0.3m,
					["gpt-4.1"] = 0.5m,
					["gemini-3-pro-preview"] = 0.5m
				}
			},
			AdditionalInfo = new Dictionary<string, object>
			{
				["isAvailable"] = true,
				["hasPremiumRequestLimit"] = true
			}
		};

		try
		{
			var execPath = GetExecutablePath();

			// Get version with timeout to avoid hanging
			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			var versionInfo = new ProcessStartInfo
			{
				FileName = execPath,
				Arguments = "--version"
			};

			// Configure for cross-platform with enhanced PATH
			PlatformHelper.ConfigureForCrossPlatform(versionInfo);

			using var versionProcess = new Process { StartInfo = versionInfo };
			versionProcess.Start();

			try
			{
				var version = await versionProcess.StandardOutput.ReadToEndAsync(linkedCts.Token);
				await versionProcess.WaitForExitAsync(linkedCts.Token);
				info.Version = version.Trim();
			}
			catch (OperationCanceledException)
			{
				try { versionProcess.Kill(entireProcessTree: true); } catch { }
				info.Version = "unknown (timeout)";
				info.AdditionalInfo["error"] = "Timed out while getting version information";
				return info;
			}

			// Check usage limits (with its own timeout handling)
			var limits = await GetUsageLimitsAsync(cancellationToken);
			if (limits.IsLimitReached)
			{
				info.AdditionalInfo["isAvailable"] = false;
				info.AdditionalInfo["unavailableReason"] = limits.Message ?? "Premium request limit reached";
			}

			if (limits.CurrentUsage.HasValue)
			{
				info.AdditionalInfo["currentUsage"] = limits.CurrentUsage.Value;
			}
			if (limits.MaxUsage.HasValue)
			{
				info.AdditionalInfo["maxUsage"] = limits.MaxUsage.Value;
			}
			if (limits.ResetTime.HasValue)
			{
				info.AdditionalInfo["resetTime"] = limits.ResetTime.Value;
			}
		}
		catch (OperationCanceledException)
		{
			info.Version = "unknown (timeout)";
			info.AdditionalInfo["error"] = "Operation was cancelled or timed out";
		}
		catch (Exception ex)
		{
			info.Version = "unknown";
			info.AdditionalInfo["error"] = ex.Message;
		}

		return info;
	}

	public override async Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
	{
		var limits = new UsageLimits
		{
			LimitType = UsageLimitType.PremiumRequests,
			IsLimitReached = false
		};

		try
		{
			var execPath = GetExecutablePath();

			// Try to get usage information via gh api or copilot status
			// GitHub Copilot CLI doesn't have a direct usage command, but we can check
			// via the GitHub CLI API or by examining response headers/errors
			var startInfo = new ProcessStartInfo
			{
				FileName = "gh",
				Arguments = "api user"
			};

			// Configure for cross-platform with enhanced PATH
			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var process = new Process { StartInfo = startInfo };

			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			process.Start();

			try
			{
				await process.WaitForExitAsync(linkedCts.Token);
			}
			catch (OperationCanceledException)
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				limits.Message = "Unable to check usage limits (timeout)";
				return limits;
			}

			var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
			var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);

			// Check for rate limit or premium request information in the response
			if (process.ExitCode == 0)
			{
				// Try to parse the response for Copilot-related information
				// The actual implementation would depend on what GitHub provides
				// For now, we assume available unless we detect a limit error
				limits.Message = "Premium requests available";

				// Check if we can get Copilot-specific limits
				var copilotLimits = await CheckCopilotLimitsAsync(cancellationToken);
				if (copilotLimits != null)
				{
					limits.CurrentUsage = copilotLimits.Value.current;
					limits.MaxUsage = copilotLimits.Value.max;
					limits.IsLimitReached = copilotLimits.Value.current >= copilotLimits.Value.max;
					limits.ResetTime = copilotLimits.Value.resetTime;

					if (limits.IsLimitReached)
					{
						limits.Message = $"Premium request limit reached ({limits.CurrentUsage}/{limits.MaxUsage}). Resets at {limits.ResetTime?.ToString("g") ?? "unknown"}";
					}
					else
					{
						limits.Message = $"Premium requests: {limits.CurrentUsage}/{limits.MaxUsage} used";
					}
				}
			}
			else if (!string.IsNullOrEmpty(error))
			{
				// Check for common limit-related error messages
				var errorLower = error.ToLowerInvariant();
				if (errorLower.Contains("rate limit") ||
					errorLower.Contains("premium") && errorLower.Contains("limit") ||
					errorLower.Contains("quota exceeded"))
				{
					limits.IsLimitReached = true;
					limits.Message = "Premium request limit reached. Please wait for the limit to reset.";
				}
				else if (errorLower.Contains("not authenticated") ||
						 errorLower.Contains("unauthorized"))
				{
					limits.Message = "Authentication required. Run 'gh auth login' to authenticate.";
				}
			}
		}
		catch (System.ComponentModel.Win32Exception)
		{
			limits.Message = "GitHub CLI (gh) not found. Install it to check usage limits.";
		}
		catch (Exception ex)
		{
			limits.Message = $"Unable to check usage limits: {ex.Message}";
		}

		return limits;
	}

	private async Task<(int current, int max, DateTime? resetTime)?> CheckCopilotLimitsAsync(CancellationToken cancellationToken)
	{
		try
		{
			// Check Copilot-specific API endpoint for usage
			var startInfo = new ProcessStartInfo
			{
				FileName = "gh",
				Arguments = "api /copilot/usage --jq '.premium_requests // empty'"
			};

			// Configure for cross-platform with enhanced PATH
			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var process = new Process { StartInfo = startInfo };

			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			process.Start();

			try
			{
				await process.WaitForExitAsync(linkedCts.Token);
			}
			catch (OperationCanceledException)
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				return null;
			}

			var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);

			if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
			{
				// Parse the JSON response for premium request limits
				try
				{
					using var doc = JsonDocument.Parse(output);
					var root = doc.RootElement;

					int current = 0;
					int max = 0;
					DateTime? resetTime = null;

					if (root.TryGetProperty("used", out var usedProp))
					{
						current = usedProp.GetInt32();
					}
					if (root.TryGetProperty("limit", out var limitProp))
					{
						max = limitProp.GetInt32();
					}
					if (root.TryGetProperty("reset_at", out var resetProp))
					{
						if (DateTime.TryParse(resetProp.GetString(), out var parsed))
						{
							resetTime = parsed;
						}
					}

					if (max > 0)
					{
						return (current, max, resetTime);
					}
				}
				catch
				{
					// JSON parsing failed, try regex patterns on raw output
				}
			}

			// Alternative: Try to parse from X-RateLimit headers in a test request
			var headerInfo = new ProcessStartInfo
			{
				FileName = "gh",
				Arguments = "api -i /user"
			};

			// Configure for cross-platform with enhanced PATH
			PlatformHelper.ConfigureForCrossPlatform(headerInfo);

			using var headerProcess = new Process { StartInfo = headerInfo };
			headerProcess.Start();
			var headerOutput = await headerProcess.StandardOutput.ReadToEndAsync(cancellationToken);
			await headerProcess.WaitForExitAsync(cancellationToken);

			if (headerProcess.ExitCode == 0)
			{
				// Parse rate limit headers
				var remainingMatch = Regex.Match(headerOutput, @"X-RateLimit-Remaining:\s*(\d+)", RegexOptions.IgnoreCase);
				var limitMatch = Regex.Match(headerOutput, @"X-RateLimit-Limit:\s*(\d+)", RegexOptions.IgnoreCase);
				var resetMatch = Regex.Match(headerOutput, @"X-RateLimit-Reset:\s*(\d+)", RegexOptions.IgnoreCase);

				if (remainingMatch.Success && limitMatch.Success)
				{
					var remaining = int.Parse(remainingMatch.Groups[1].Value);
					var limit = int.Parse(limitMatch.Groups[1].Value);
					DateTime? reset = null;

					if (resetMatch.Success)
					{
						var resetUnix = long.Parse(resetMatch.Groups[1].Value);
						reset = DateTimeOffset.FromUnixTimeSeconds(resetUnix).UtcDateTime;
					}

					return (limit - remaining, limit, reset);
				}
			}
		}
		catch
		{
			// Ignore errors in optional limit checking
		}

		return null;
	}

	private string GetExecutablePath()
	{
		// Use PlatformHelper for cross-platform executable resolution
		var basePath = !string.IsNullOrEmpty(_executablePath) ? _executablePath : "copilot";
		return PlatformHelper.ResolveExecutablePath(basePath, _executablePath);
	}

	private static string EscapeArgument(string argument)
	{
		return PlatformHelper.EscapeArgument(argument).Trim('"', '\'');
	}

	public override Task<SessionSummary> GetSessionSummaryAsync(
		string? sessionId,
		string? workingDirectory = null,
		string? fallbackOutput = null,
		CancellationToken cancellationToken = default)
	{
		var summary = new SessionSummary();

		// GitHub Copilot CLI doesn't have persistent sessions like Claude
		// We rely on the fallback output to generate a summary

		if (!string.IsNullOrEmpty(fallbackOutput))
		{
			summary.Summary = GenerateSummaryFromOutput(fallbackOutput);
			summary.Success = !string.IsNullOrEmpty(summary.Summary);
			summary.Source = "output";
			return Task.FromResult(summary);
		}

		summary.Success = false;
		summary.ErrorMessage = "GitHub Copilot CLI does not support session persistence. No output available to generate summary.";
		return Task.FromResult(summary);
	}

	/// <summary>
	/// Generates a summary from execution output
	/// </summary>
	private static string GenerateSummaryFromOutput(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
			return string.Empty;

		var lines = output.Split('\n');
		var significantActions = new List<string>();

		foreach (var line in lines)
		{
			var trimmed = line.Trim();

			// Skip empty lines and JSON
			if (string.IsNullOrWhiteSpace(trimmed)) continue;
			if (trimmed.StartsWith("{") || trimmed.StartsWith("[")) continue;
			if (trimmed.Length < 10) continue;

			// Look for action-oriented statements
			if (trimmed.Contains("created", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("modified", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("updated", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("added", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("removed", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("fixed", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("implemented", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("refactored", StringComparison.OrdinalIgnoreCase))
			{
				if (trimmed.Length < 200)
				{
					significantActions.Add(trimmed);
				}
			}
		}

		if (significantActions.Count > 0)
		{
			return string.Join("; ", significantActions.Take(3));
		}

		// Fallback: return first meaningful line
		foreach (var line in lines)
		{
			var trimmed = line.Trim();
			if (!string.IsNullOrWhiteSpace(trimmed) &&
				!trimmed.StartsWith("{") &&
				!trimmed.StartsWith("[") &&
				trimmed.Length >= 20 &&
				trimmed.Length <= 200)
			{
				return trimmed;
			}
		}

		return string.Empty;
	}

	public override async Task<PromptResponse> GetPromptResponseAsync(
		string prompt,
		string? workingDirectory = null,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		var execPath = GetExecutablePath();
		if (string.IsNullOrEmpty(execPath))
		{
			return PromptResponse.Fail("GitHub Copilot CLI executable path is not configured.");
		}

		try
		{
			var effectiveWorkingDir = workingDirectory ?? _workingDirectory ?? Environment.CurrentDirectory;

			// Use -p flag for simple non-interactive prompt response
			// copilot -p "your basic prompt"
			var args = $"-p \"{EscapeArgument(prompt)}\"";

			var startInfo = new ProcessStartInfo
			{
				FileName = execPath,
				Arguments = args,
				WorkingDirectory = effectiveWorkingDir
			};

			// Configure for cross-platform with enhanced PATH
			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var process = new Process { StartInfo = startInfo };

			// Use a reasonable timeout for simple prompts (2 minutes)
			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			process.Start();

			// Close stdin immediately
			try { process.StandardInput.Close(); } catch { }

			var output = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
			var error = await process.StandardError.ReadToEndAsync(linkedCts.Token);

			try
			{
				await process.WaitForExitAsync(linkedCts.Token);
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				return PromptResponse.Fail("Request timed out after 2 minutes.");
			}

			stopwatch.Stop();

			if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
			{
				return PromptResponse.Fail($"GitHub Copilot CLI returned error: {error}");
			}

			return PromptResponse.Ok(output.Trim(), stopwatch.ElapsedMilliseconds, "copilot");
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			return PromptResponse.Fail($"Failed to start GitHub Copilot CLI: {ex.Message}");
		}
		catch (OperationCanceledException)
		{
			return PromptResponse.Fail("Request was cancelled.");
		}
		catch (Exception ex)
		{
			return PromptResponse.Fail($"Error executing GitHub Copilot CLI: {ex.Message}");
		}
	}

	// JSON models for Copilot CLI output
	// Note: JsonPropertyName attributes support both camelCase and snake_case from CLI
	private class CopilotStreamEvent
	{
		[JsonPropertyName("type")]
		public string? Type { get; set; }

		[JsonPropertyName("content")]
		public string? Content { get; set; }

		[JsonPropertyName("suggestion")]
		public string? Suggestion { get; set; }

		[JsonPropertyName("error")]
		public string? Error { get; set; }

		[JsonPropertyName("message")]
		public string? Message { get; set; }

		// Token usage fields
		[JsonPropertyName("input_tokens")]
		public int? InputTokens { get; set; }

		[JsonPropertyName("output_tokens")]
		public int? OutputTokens { get; set; }

		[JsonPropertyName("cost_usd")]
		public decimal? CostUsd { get; set; }

		[JsonPropertyName("total_cost_usd")]
		public decimal? TotalCostUsd { get; set; }

		[JsonPropertyName("usage")]
		public CopilotUsageInfo? Usage { get; set; }

		[JsonPropertyName("model")]
		public string? Model { get; set; }
	}

	private class CopilotUsageInfo
	{
		[JsonPropertyName("input_tokens")]
		public int? InputTokens { get; set; }

		[JsonPropertyName("output_tokens")]
		public int? OutputTokens { get; set; }

		[JsonPropertyName("total_tokens")]
		public int? TotalTokens { get; set; }
	}
}
