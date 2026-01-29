using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeSwarm.Shared.Providers.Copilot;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Provider implementation for GitHub Copilot CLI.
/// </summary>
public class CopilotProvider : CliProviderBase
{
    private const string DefaultExecutable = "copilot";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public override ProviderType Type => ProviderType.Copilot;

    public CopilotProvider(Provider config)
        : base(config.Id, config.Name, ProviderConnectionMode.CLI, config.ExecutablePath, config.WorkingDirectory)
    {
        // Copilot CLI only supports CLI mode (no REST API)
        ConnectionMode = ProviderConnectionMode.CLI;
    }

    private string GetExecutablePath() => ResolveExecutablePath(DefaultExecutable);

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await TestCliConnectionAsync(GetExecutablePath(), "GitHub Copilot", cancellationToken: cancellationToken);
        }
        catch
        {
            IsConnected = false;
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
        var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;

        // Build arguments for non-interactive execution
        var args = new List<string>
        {
            "-p",
            $"\"{EscapeCliArgument(prompt)}\"",
            "--allow-all-tools"
        };

        if (!string.IsNullOrEmpty(CurrentMcpConfigPath))
        {
            args.Add("--additional-mcp-config");
            args.Add($"\"@{CurrentMcpConfigPath}\"");
        }

        if (CurrentAdditionalArgs != null)
        {
            args.AddRange(CurrentAdditionalArgs);
        }

        using var process = CreateCliProcess(execPath, string.Join(" ", args), effectiveWorkingDir);

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
        ReportProcessStarted(process.Id, progress);

        // Start initialization monitor
        using var initMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var initializationMonitorTask = CreateInitializationMonitorAsync(
            () => outputBuilder.Count > 0,
            progress,
            initMonitorCts.Token);

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                outputComplete.TrySetResult(true);
                return;
            }

            if (string.IsNullOrEmpty(e.Data)) return;

            lock (outputBuilder)
            {
                outputBuilder.Add(e.Data);
            }

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

                progress?.Report(new ExecutionProgress
                {
                    OutputLine = e.Data,
                    IsErrorOutput = true
                });

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

        await WaitForProcessExitAsync(process, initMonitorCts, cancellationToken);

        try { await initializationMonitorTask; } catch (OperationCanceledException) { }

        await WaitForOutputStreamsAsync(outputComplete, errorComplete);

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

        // Parse usage data from stderr
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

        var usagePattern = new Regex(
            @"(\d+(?:\.\d+)?)\s*k?\s*in\s*,\s*(\d+(?:\.\d+)?)\s*k?\s*out",
            RegexOptions.IgnoreCase);

        var match = usagePattern.Match(stderr);
        if (match.Success)
        {
            if (double.TryParse(match.Groups[1].Value, out var inputValue))
            {
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

        var modelPattern = new Regex(@"^\s*(?:\[ERR\])?\s*(claude-[\w.-]+|gpt-[\w.-]+|gemini-[\w.-]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var modelMatch = modelPattern.Match(stderr);
        if (modelMatch.Success && string.IsNullOrEmpty(result.ModelUsed))
        {
            result.ModelUsed = modelMatch.Groups[1].Value.Trim();
        }

        var premiumPattern = new Regex(@"Est\.\s*(\d+)\s*Premium\s*requests?", RegexOptions.IgnoreCase);
        var premiumMatch = premiumPattern.Match(stderr);
        if (premiumMatch.Success && int.TryParse(premiumMatch.Groups[1].Value, out var premiumRequests))
        {
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

        var args = $"-p \"{EscapeCliArgument(prompt)}\" --allow-all-tools";

        var startInfo = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = args
        };

        PlatformHelper.ConfigureForCrossPlatform(startInfo);

        if (!string.IsNullOrEmpty(WorkingDirectory))
        {
            startInfo.WorkingDirectory = WorkingDirectory;
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
            info.Version = await GetCliVersionAsync(GetExecutablePath(), cancellationToken: cancellationToken);

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
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "api user"
            };

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

            if (process.ExitCode == 0)
            {
                limits.Message = "Premium requests available";

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
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "api /copilot/usage --jq '.premium_requests // empty'"
            };

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
                    // JSON parsing failed
                }
            }

            // Alternative: Try to parse from X-RateLimit headers
            var headerInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "api -i /user"
            };

            PlatformHelper.ConfigureForCrossPlatform(headerInfo);

            using var headerProcess = new Process { StartInfo = headerInfo };
            headerProcess.Start();
            var headerOutput = await headerProcess.StandardOutput.ReadToEndAsync(cancellationToken);
            await headerProcess.WaitForExitAsync(cancellationToken);

            if (headerProcess.ExitCode == 0)
            {
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

    public override async Task<PromptResponse> GetPromptResponseAsync(
        string prompt,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var execPath = GetExecutablePath();
        if (string.IsNullOrEmpty(execPath))
        {
            return PromptResponse.Fail("GitHub Copilot CLI executable path is not configured.");
        }

        var args = $"-p \"{EscapeCliArgument(prompt)}\"";
        return await ExecuteSimplePromptAsync(execPath, args, "GitHub Copilot", workingDirectory, cancellationToken);
    }
}
