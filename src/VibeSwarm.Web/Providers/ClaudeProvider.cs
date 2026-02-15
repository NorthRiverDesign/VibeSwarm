using System.Diagnostics;
using System.Text.Json;
using VibeSwarm.Shared.Providers.Claude;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Provider implementation for Claude CLI mode.
/// For SDK mode, use ClaudeSdkProvider instead.
/// </summary>
public class ClaudeProvider : CliProviderBase
{
    private const string DefaultExecutable = "claude";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public override ProviderType Type => ProviderType.Claude;

    public ClaudeProvider(Provider config)
        : base(config.Id, config.Name, config.ConnectionMode, config.ExecutablePath, config.WorkingDirectory)
    {
    }

    private string GetExecutablePath() => ResolveExecutablePath(DefaultExecutable);

    protected override string? GetUpdateCommand() => GetExecutablePath();
    protected override string GetUpdateArguments() => "update";
    protected override string? GetDefaultExecutablePath() => GetExecutablePath();

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await TestCliConnectionAsync(GetExecutablePath(), "Claude", cancellationToken: cancellationToken);
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    public override async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        return await ExecuteCliAsync(prompt, null, cancellationToken);
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
                ErrorMessage = "Claude executable path is not configured."
            };
        }

        var result = new ExecutionResult { Messages = new List<ExecutionMessage>() };
        var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;

        // Reset accumulated token counters for this execution
        _accumulatedInputTokens = 0;
        _accumulatedOutputTokens = 0;
        _hasAccumulatedTokens = false;

        // Build arguments for non-interactive execution with JSON streaming output
        var args = new List<string>
        {
            "-p",
            $"\"{EscapeCliArgument(prompt)}\"",
            "--output-format",
            "stream-json",
            "--verbose",
            "--dangerously-skip-permissions"
        };

        if (!string.IsNullOrEmpty(sessionId))
        {
            args.Add("--resume");
            args.Add(sessionId);
        }

        if (!string.IsNullOrEmpty(CurrentMcpConfigPath))
        {
            args.Add("--mcp-config");
            args.Add($"\"{CurrentMcpConfigPath}\"");
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
                ErrorMessage = $"Failed to start Claude CLI process: {ex.Message}. " +
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

            try
            {
                var jsonEvent = JsonSerializer.Deserialize<ClaudeStreamEvent>(e.Data, JsonOptions);
                if (jsonEvent != null)
                {
                    ProcessStreamEvent(jsonEvent, result, currentAssistantMessage, progress);
                }
            }
            catch
            {
                currentAssistantMessage.Append(e.Data);
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

        // Parse usage limit signals from stderr
        if (!string.IsNullOrEmpty(error))
        {
            var usageLimits = ClaudeUsageParser.ParseLimitSignals(error);
            if (usageLimits != null)
            {
                result.DetectedUsageLimits = usageLimits;
            }
        }

        // Final fallback: if no result event provided tokens, use accumulated from assistant messages
        if (!result.InputTokens.HasValue && _hasAccumulatedTokens && _accumulatedInputTokens > 0)
        {
            result.InputTokens = _accumulatedInputTokens;
        }
        if (!result.OutputTokens.HasValue && _hasAccumulatedTokens && _accumulatedOutputTokens > 0)
        {
            result.OutputTokens = _accumulatedOutputTokens;
        }

        return result;
    }

    // Accumulated token usage across all assistant message events
    private int _accumulatedInputTokens = 0;
    private int _accumulatedOutputTokens = 0;
    private bool _hasAccumulatedTokens = false;

    private void ProcessStreamEvent(
        ClaudeStreamEvent evt,
        ExecutionResult result,
        System.Text.StringBuilder currentMessage,
        IProgress<ExecutionProgress>? progress)
    {
        switch (evt.Type)
        {
            case "system":
                if (!string.IsNullOrEmpty(evt.SessionId))
                {
                    result.SessionId = evt.SessionId;
                }
                progress?.Report(new ExecutionProgress
                {
                    CurrentMessage = "Initializing...",
                    IsStreaming = false
                });
                break;

            case "assistant":
                // Accumulate token usage from each assistant message event
                if (evt.Message?.Usage != null)
                {
                    if (evt.Message.Usage.InputTokens.HasValue)
                    {
                        _accumulatedInputTokens += evt.Message.Usage.InputTokens.Value;
                        _hasAccumulatedTokens = true;
                    }
                    if (evt.Message.Usage.OutputTokens.HasValue)
                    {
                        _accumulatedOutputTokens += evt.Message.Usage.OutputTokens.Value;
                        _hasAccumulatedTokens = true;
                    }
                }
                if (evt.Message?.Content != null)
                {
                    if (!string.IsNullOrEmpty(evt.Message.Model))
                    {
                        result.ModelUsed = evt.Message.Model;
                    }

                    foreach (var content in evt.Message.Content)
                    {
                        if (content.Type == "text" && !string.IsNullOrEmpty(content.Text))
                        {
                            currentMessage.Append(content.Text);
                            progress?.Report(new ExecutionProgress
                            {
                                CurrentMessage = content.Text.Length > 100
                                    ? content.Text[..100] + "..."
                                    : content.Text,
                                IsStreaming = true
                            });
                        }
                        else if (content.Type == "tool_use")
                        {
                            if (currentMessage.Length > 0)
                            {
                                result.Messages.Add(new ExecutionMessage
                                {
                                    Role = "assistant",
                                    Content = currentMessage.ToString(),
                                    Timestamp = DateTime.UtcNow
                                });
                                currentMessage.Clear();
                            }

                            result.Messages.Add(new ExecutionMessage
                            {
                                Role = "tool_use",
                                Content = content.Name ?? "unknown",
                                ToolName = content.Name,
                                ToolInput = content.Input?.ToString(),
                                Timestamp = DateTime.UtcNow
                            });
                            progress?.Report(new ExecutionProgress
                            {
                                ToolName = content.Name,
                                IsStreaming = false
                            });
                        }
                    }
                }
                if (!string.IsNullOrEmpty(evt.SessionId))
                {
                    result.SessionId = evt.SessionId;
                }
                break;

            case "user":
                if (evt.Message?.Content != null)
                {
                    foreach (var content in evt.Message.Content)
                    {
                        if (content.Type == "tool_result")
                        {
                            result.Messages.Add(new ExecutionMessage
                            {
                                Role = "tool_result",
                                Content = content.Content ?? "",
                                ToolName = content.ToolUseId,
                                ToolOutput = content.Content,
                                Timestamp = DateTime.UtcNow
                            });
                        }
                    }
                }
                break;

            case "result":
                if (evt.TotalCostUsd.HasValue)
                {
                    result.CostUsd = evt.TotalCostUsd;
                }
                else if (evt.CostUsd.HasValue)
                {
                    result.CostUsd = evt.CostUsd;
                }
                // Try nested usage object first
                if (evt.Usage != null)
                {
                    result.InputTokens = evt.Usage.InputTokens;
                    result.OutputTokens = evt.Usage.OutputTokens;
                }
                // Fall back to flat token fields on the result event
                if (!result.InputTokens.HasValue && evt.InputTokens.HasValue)
                {
                    result.InputTokens = evt.InputTokens;
                }
                if (!result.OutputTokens.HasValue && evt.OutputTokens.HasValue)
                {
                    result.OutputTokens = evt.OutputTokens;
                }
                // Fall back to accumulated tokens from assistant message events
                if (!result.InputTokens.HasValue && _hasAccumulatedTokens)
                {
                    result.InputTokens = _accumulatedInputTokens;
                }
                if (!result.OutputTokens.HasValue && _hasAccumulatedTokens)
                {
                    result.OutputTokens = _accumulatedOutputTokens;
                }
                if (!string.IsNullOrEmpty(evt.SessionId))
                {
                    result.SessionId = evt.SessionId;
                }
                if (!string.IsNullOrEmpty(evt.Result))
                {
                    result.Output = evt.Result;
                }
                break;
        }
    }

    private async Task<string> ExecuteCliAsync(string prompt, string? sessionId, CancellationToken cancellationToken)
    {
        var execPath = GetExecutablePath();
        if (string.IsNullOrEmpty(execPath))
        {
            throw new InvalidOperationException("Claude executable path is not configured.");
        }

        var args = $"-p \"{EscapeCliArgument(prompt)}\"";
        if (!string.IsNullOrEmpty(sessionId))
        {
            args += $" --resume {sessionId}";
        }

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
            throw new InvalidOperationException($"Claude execution failed: {error}");
        }

        return output;
    }

    public override async Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = new ProviderInfo
        {
            AvailableModels = new List<string>
            {
                "sonnet",
                "opus",
                "haiku",
                "claude-sonnet-4-5-20250929",
                "claude-opus-4-20250514",
                "claude-3-5-haiku-20241022"
            },
            AvailableAgents = new List<AgentInfo>
            {
                new() { Name = "default", Description = "Default Claude Code agent with full capabilities", IsDefault = true }
            },
            Pricing = new PricingInfo
            {
                InputTokenPricePerMillion = 3.00m,
                OutputTokenPricePerMillion = 15.00m,
                Currency = "USD",
                ModelMultipliers = new Dictionary<string, decimal>
                {
                    ["sonnet"] = 1.0m,
                    ["opus"] = 5.0m,
                    ["haiku"] = 0.27m,
                    ["claude-sonnet-4-5-20250929"] = 1.0m,
                    ["claude-opus-4-20250514"] = 5.0m,
                    ["claude-3-5-haiku-20241022"] = 0.27m
                }
            },
            AdditionalInfo = new Dictionary<string, object>
            {
                ["isAvailable"] = true
            }
        };

        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            info.Version = await GetCliVersionAsync(GetExecutablePath(), cancellationToken: cancellationToken);
        }

        return info;
    }

    public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
    {
        var limits = new UsageLimits
        {
            LimitType = UsageLimitType.SessionLimit,
            IsLimitReached = false,
            Message = "Session limits available via Claude CLI"
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

        if (!string.IsNullOrEmpty(sessionId) && ConnectionMode == ProviderConnectionMode.CLI)
        {
            try
            {
                var execPath = GetExecutablePath();
                var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;

                var summarizePrompt = "Please provide a concise summary (1-2 sentences) of what was accomplished in this session, suitable for a git commit message. Focus on the key changes made.";
                var args = $"--resume {sessionId} -p \"{EscapeCliArgument(summarizePrompt)}\" --max-turns 1";

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
                        var cleanedOutput = CleanSummaryOutput(output);
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
        summary.ErrorMessage = "No session ID or output available to generate summary";
        return summary;
    }

    private static string CleanSummaryOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        try
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var textContent = new System.Text.StringBuilder();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeProp) &&
                        typeProp.GetString() == "assistant")
                    {
                        if (root.TryGetProperty("message", out var messageProp) &&
                            messageProp.TryGetProperty("content", out var contentProp))
                        {
                            foreach (var content in contentProp.EnumerateArray())
                            {
                                if (content.TryGetProperty("type", out var contentType) &&
                                    contentType.GetString() == "text" &&
                                    content.TryGetProperty("text", out var textProp))
                                {
                                    textContent.Append(textProp.GetString());
                                }
                            }
                        }
                    }
                    else if (root.TryGetProperty("type", out var resultType) &&
                             resultType.GetString() == "result" &&
                             root.TryGetProperty("result", out var resultProp))
                    {
                        var result = resultProp.GetString();
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            textContent.Clear();
                            textContent.Append(result);
                        }
                    }
                }
                catch
                {
                    textContent.AppendLine(line);
                }
            }

            var text = textContent.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        catch
        {
            // Not JSON format
        }

        return output.Trim();
    }

    public override async Task<PromptResponse> GetPromptResponseAsync(
        string prompt,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var execPath = GetExecutablePath();
        if (string.IsNullOrEmpty(execPath))
        {
            return PromptResponse.Fail("Claude executable path is not configured.");
        }

        var args = $"-p \"{EscapeCliArgument(prompt)}\"";
        return await ExecuteSimplePromptAsync(execPath, args, "Claude", workingDirectory, cancellationToken);
    }
}
