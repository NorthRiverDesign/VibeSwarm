using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

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
				Arguments = "--version",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = _workingDirectory ?? Environment.CurrentDirectory
			};

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
			LastConnectionError = $"Failed to start GitHub Copilot CLI: {ex.Message}. " +
				$"Executable path: '{execPath}'. " +
				$"Ensure the GitHub Copilot CLI is installed and available in your PATH.";
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
		// --output-format stream-json: JSON streaming output for parsing progress
		// --dangerously-skip-permissions: skip permission prompts (auto-accept tool use)
		var args = new List<string>();

		// Add the prompt with -p flag for non-interactive mode
		args.Add("-p");
		args.Add($"\"{EscapeArgument(prompt)}\"");

		// Add output format flags for streaming JSON output
		args.Add("--output-format");
		args.Add("stream-json");

		// Skip permission prompts for automated execution
		args.Add("--dangerously-skip-permissions");

		var startInfo = new ProcessStartInfo
		{
			FileName = execPath,
			Arguments = string.Join(" ", args),
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = effectiveWorkingDir,
			RedirectStandardInput = true
		};

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

		try
		{
			await process.WaitForExitAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			try { process.Kill(entireProcessTree: true); } catch { }
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

		return result;
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

			case "done":
			case "complete":
				// Finalize
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

		// Use -p for non-interactive mode and --dangerously-skip-permissions for automated execution
		var args = $"-p \"{EscapeArgument(prompt)}\" --dangerously-skip-permissions";

		var startInfo = new ProcessStartInfo
		{
			FileName = execPath,
			Arguments = args,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = _workingDirectory ?? Environment.CurrentDirectory
		};

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
			AvailableModels = new List<string>
			{
				"gpt-4o",
				"claude-sonnet-4",
				"gemini-2.0-flash"
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
					["gpt-4o"] = 1.0m,
					["claude-sonnet-4"] = 1.0m,
					["gemini-2.0-flash"] = 0.5m
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

			// Get version
			var versionInfo = new ProcessStartInfo
			{
				FileName = execPath,
				Arguments = "--version",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			using var versionProcess = new Process { StartInfo = versionInfo };
			versionProcess.Start();
			var version = await versionProcess.StandardOutput.ReadToEndAsync(cancellationToken);
			await versionProcess.WaitForExitAsync(cancellationToken);
			info.Version = version.Trim();

			// Check usage limits
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
				Arguments = "api user",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

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
				Arguments = "api /copilot/usage --jq '.premium_requests // empty'",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

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
				Arguments = "api -i /user",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

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
		if (!string.IsNullOrEmpty(_executablePath))
		{
			return _executablePath;
		}
		// Default to standalone 'copilot' CLI command
		return "copilot";
	}

	private static string EscapeArgument(string argument)
	{
		return argument.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}

	// JSON models for Copilot CLI output
	private class CopilotStreamEvent
	{
		public string? Type { get; set; }
		public string? Content { get; set; }
		public string? Suggestion { get; set; }
		public string? Error { get; set; }
		public string? Message { get; set; }
	}
}
