using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeSwarm.Shared.Providers.Copilot;
using VibeSwarm.Shared.Services;
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

    protected override string? GetUpdateCommand() => GetExecutablePath();
    protected override string GetUpdateArguments() => "update";
    protected override string? GetDefaultExecutablePath() => GetExecutablePath();

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
        // Reference: https://github.com/github/copilot-cli/blob/main/changelog.md
        var args = new List<string>
        {
            "-p",
            $"\"{EscapeCliArgument(prompt)}\"",
            "--yolo",    // Auto-approve all tool permissions (v0.0.381+)
            "--silent"   // Suppress stats output for cleaner capture (v0.0.365+)
        };

        // Session resume support (v0.0.372+)
        if (!string.IsNullOrEmpty(sessionId))
        {
            args.Add("--resume");
            args.Add(sessionId);
        }
        else if (CurrentContinueLastSession)
        {
            args.Add("--continue");
        }

        // Model selection (v0.0.329+)
        if (!string.IsNullOrEmpty(CurrentModel))
        {
            args.Add("--model");
            args.Add(CurrentModel);
        }

        // Agent selection (v0.0.353+ for custom agents, v0.0.380+ in interactive)
        if (!string.IsNullOrEmpty(CurrentAgent))
        {
            args.Add("--agent");
            args.Add(CurrentAgent);
        }

        // System prompt override (v0.0.397+)
        if (!string.IsNullOrEmpty(CurrentSystemPrompt))
        {
            args.Add("--system-prompt");
            args.Add($"\"{EscapeCliArgument(CurrentSystemPrompt)}\"");
        }

        // Autopilot mode for autonomous task completion (v0.0.400+, GA v0.0.411+)
        if (CurrentUseAutopilot)
        {
            args.Add("--autopilot");
        }

        // Alt-screen buffer mode (v0.0.407+, experimental)
        if (CurrentUseAltScreen)
        {
            args.Add("--alt-screen");
            args.Add("on");
        }

        // Tool filtering (v0.0.370+)
        if (CurrentAllowedTools != null && CurrentAllowedTools.Count > 0)
        {
            foreach (var tool in CurrentAllowedTools)
            {
                args.Add("--available-tools");
                args.Add(tool);
            }
        }
        if (CurrentExcludedTools != null && CurrentExcludedTools.Count > 0)
        {
            foreach (var tool in CurrentExcludedTools)
            {
                args.Add("--excluded-tools");
                args.Add(tool);
            }
        }

        if (!string.IsNullOrEmpty(CurrentMcpConfigPath))
        {
            args.Add("--additional-mcp-config");
            args.Add($"\"@{CurrentMcpConfigPath}\"");
        }

        if (CurrentAdditionalArgs != null)
        {
            args.AddRange(CurrentAdditionalArgs);
        }

        var fullArguments = string.Join(" ", args);
        var fullCommand = $"{execPath} {fullArguments}";

        using var process = CreateCliProcess(execPath, fullArguments, effectiveWorkingDir);

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
        result.CommandUsed = fullCommand;
        ReportProcessStarted(process.Id, progress, fullCommand);

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

        // Parse premium requests consumed (Copilot-specific)
        var premiumPattern = new Regex(@"Est\.\s*(\d+)\s*Premium\s*requests?", RegexOptions.IgnoreCase);
        var premiumMatch = premiumPattern.Match(stderr);
        if (premiumMatch.Success && int.TryParse(premiumMatch.Groups[1].Value, out var premiumRequests))
        {
            result.PremiumRequestsConsumed = premiumRequests;
        }
    }

    private void ProcessStreamEvent(
        CopilotStreamEvent evt,
        ExecutionResult result,
        System.Text.StringBuilder currentMessage,
        IProgress<ExecutionProgress>? progress)
    {
        // Capture session ID from any event that includes it (v0.0.372+)
        if (!string.IsNullOrEmpty(evt.SessionId) && string.IsNullOrEmpty(result.SessionId))
        {
            result.SessionId = evt.SessionId;
        }

        // Track premium request usage
        if (evt.PremiumRequests.HasValue)
        {
            result.PremiumRequestsConsumed = evt.PremiumRequests;
        }

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

        var args = $"-p \"{EscapeCliArgument(prompt)}\" --yolo --silent";

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
                "claude-opus-4.6",
                "claude-opus-4.6-fast",
                "claude-sonnet-4.6",
                "claude-sonnet-4.5",
                "claude-haiku-4.5",
                "claude-opus-4.5",
                "claude-sonnet-4",
                "gpt-5.2-codex",
                "gpt-5.2",
                "gpt-5.1-codex-max",
                "gpt-5.1-codex",
                "gpt-5.1",
                "gpt-5.1-codex-mini",
                "gpt-5-mini",
                "gpt-4.1"
            },
            AvailableAgents = new List<AgentInfo>
            {
                new() { Name = "default", Description = "Default coding agent with full capabilities", IsDefault = true }
            },
            Pricing = new PricingInfo
            {
                Currency = "USD",
                ModelMultipliers = new Dictionary<string, decimal>
                {
                    ["claude-opus-4.6"] = 5.0m,
                    ["claude-opus-4.6-fast"] = 3.0m,
                    ["claude-sonnet-4.6"] = 1.0m,
                    ["claude-sonnet-4.5"] = 1.0m,
                    ["claude-haiku-4.5"] = 0.2m,
                    ["claude-opus-4.5"] = 5.0m,
                    ["claude-sonnet-4"] = 1.0m,
                    ["gpt-5.2-codex"] = 1.5m,
                    ["gpt-5.2"] = 1.5m,
                    ["gpt-5.1-codex-max"] = 2.0m,
                    ["gpt-5.1-codex"] = 1.0m,
                    ["gpt-5.1"] = 1.0m,
                    ["gpt-5.1-codex-mini"] = 0.3m,
                    ["gpt-5-mini"] = 0.3m,
                    ["gpt-4.1"] = 0.5m
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

    public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
    {
        // Copilot CLI is a standalone binary and doesn't have an API to query usage limits.
        // Usage tracking is done by parsing stderr output during job execution.
        // The user can configure their plan's limit (e.g., 300 premium requests/month for Copilot Pro)
        // in the provider settings, and we track against that.
        var limits = new UsageLimits
        {
            LimitType = UsageLimitType.PremiumRequests,
            IsLimitReached = false,
            Message = "Premium request usage is tracked per-job execution. Configure your plan's limit in provider settings."
        };

        return Task.FromResult(limits);
    }

    public override async Task<SessionSummary> GetSessionSummaryAsync(
        string? sessionId,
        string? workingDirectory = null,
        string? fallbackOutput = null,
        CancellationToken cancellationToken = default)
    {
        var summary = new SessionSummary();

        // GitHub Copilot CLI supports sessions since v0.0.372 (--resume) and v0.0.333 (--continue).
        // Attempt to resume the session and ask for a summary.
        if (!string.IsNullOrEmpty(sessionId) && ConnectionMode == ProviderConnectionMode.CLI)
        {
            try
            {
                var execPath = GetExecutablePath();
                var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;

                var summarizePrompt = "Please provide a concise summary (1-2 sentences) of what was accomplished in this session, suitable for a git commit message. Focus on the key changes made.";
                var args = $"--resume {sessionId} -p \"{EscapeCliArgument(summarizePrompt)}\" --yolo --silent";

                var startInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = args,
                    WorkingDirectory = effectiveWorkingDir
                };

                PlatformHelper.ConfigureForCrossPlatform(startInfo);

                using var process = new Process { StartInfo = startInfo };
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    process.Start();
                    process.StandardInput.Close();

                    var output = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                    await process.WaitForExitAsync(linkedCts.Token);

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        var cleanedOutput = output.Trim();
                        if (!string.IsNullOrWhiteSpace(cleanedOutput))
                        {
                            summary.Success = true;
                            summary.Summary = cleanedOutput;
                            summary.Source = "session";
                            return summary;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    try { PlatformHelper.TryKillProcessTree(process.Id); } catch { }
                }
            }
            catch
            {
                // Fall through to fallback
            }
        }

        if (!string.IsNullOrEmpty(fallbackOutput))
        {
            summary.Summary = GenerateSummaryFromOutput(fallbackOutput);
            summary.Success = !string.IsNullOrEmpty(summary.Summary);
            summary.Source = "output";
            return summary;
        }

        summary.Success = false;
        summary.ErrorMessage = "No session ID or output available to generate summary.";
        return summary;
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
