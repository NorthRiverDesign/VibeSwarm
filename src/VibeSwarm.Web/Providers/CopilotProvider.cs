using System.Diagnostics;
using System.Text.Json;
using GitHub.Copilot.SDK;
using VibeSwarm.Shared.Providers.Claude;
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
    private UsageLimits? _lastObservedUsageLimits;

    // System error detection during stream parsing (mirrors ClaudeProvider pattern)
    private bool _systemErrorDetected;
    private string? _systemErrorMessage;
    private readonly Dictionary<string, string> _toolNamesById = new(StringComparer.Ordinal);

    // Accumulated token usage from Claude-format assistant message events
    private int _accumulatedInputTokens;
    private int _accumulatedOutputTokens;
    private bool _hasAccumulatedTokens;

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

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            BaseEnvironmentVariables = new Dictionary<string, string>
            {
                ["GH_TOKEN"] = config.ApiKey,
                ["GITHUB_TOKEN"] = config.ApiKey
            };
        }
    }

    private string GetExecutablePath() => ResolveExecutablePath(DefaultExecutable);

    protected override string? GetUpdateCommand() => GetExecutablePath();
    protected override string GetUpdateArguments() => "update";
    protected override string? GetDefaultExecutablePath() => GetExecutablePath();

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await TestCliConnectionAsync(GetExecutablePath(), "GitHub Copilot", "--binary-version", cancellationToken);
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

        // Reset system error and token accumulation state for this execution
        _systemErrorDetected = false;
        _systemErrorMessage = null;
        _accumulatedInputTokens = 0;
        _accumulatedOutputTokens = 0;
        _hasAccumulatedTokens = false;
        _toolNamesById.Clear();

        // Copilot CLI does not support --system-prompt or --append-system-prompt flags.
        // Prepend any system prompt content into the main prompt using XML tags so the
        // agent still receives efficiency rules, build verification, and project memory.
        var effectivePrompt = BuildEffectivePrompt(prompt, CurrentSystemPrompt, CurrentAppendSystemPrompt);

        // Build arguments for non-interactive execution
        // Reference: https://github.com/github/copilot-cli/blob/main/changelog.md
        var args = new List<string>
        {
            "-p",
            effectivePrompt,
            "--yolo",       // Auto-approve all tool permissions (v0.0.381+)
            "--silent",     // Suppress stats output for cleaner capture (v0.0.365+)
            "--autopilot"   // Autonomous task completion mode (v0.0.400+, GA v0.0.411+)
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

        // Max autopilot continues limit
        if (CurrentMaxTurns.HasValue)
        {
            args.Add("--max-autopilot-continues");
            args.Add(CurrentMaxTurns.Value.ToString());
        }

        // Reasoning effort level (GA v0.0.411+)
        var reasoningEffort = NormalizeReasoningEffort(CurrentReasoningEffort, "low", "medium", "high");
        if (!string.IsNullOrEmpty(reasoningEffort))
        {
            args.Add("--reasoning-effort");
            args.Add(reasoningEffort);
        }

        // Alt-screen buffer mode (v0.0.407+, experimental)
        if (CurrentUseAltScreen)
        {
            args.Add("--alt-screen");
            args.Add("on");
        }

        // Bash environment file (v0.0.418+)
        if (!string.IsNullOrEmpty(CurrentBashEnvPath))
        {
            args.Add("--bash-env");
            args.Add(CurrentBashEnvPath);
        }

        // Disable mouse input for headless/non-interactive jobs
        args.Add("--no-mouse");

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

        // Denied tools
        if (CurrentDisallowedTools != null && CurrentDisallowedTools.Count > 0)
        {
            foreach (var tool in CurrentDisallowedTools)
            {
                args.Add("--deny-tool");
                args.Add(tool);
            }
        }

        if (!string.IsNullOrEmpty(CurrentMcpConfigPath))
        {
            args.Add("--additional-mcp-config");
            args.Add($"@{CurrentMcpConfigPath}");
        }

        if (CurrentAdditionalArgs != null)
        {
            args.AddRange(CurrentAdditionalArgs);
        }

        var fullCommand = FormatCommandForDisplay(execPath, args);

        using var process = CreateCliProcess(execPath, args, effectiveWorkingDir);

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
                    ProcessStreamEvent(jsonEvent, result, currentAssistantMessage, progress, _toolNamesById);
                }
            }
            catch
            {
                // Only append non-JSON lines as assistant content.
                // JSON lines that failed deserialization are already captured in
                // outputBuilder for Console Output — don't duplicate as chat messages.
                if (!e.Data.TrimStart().StartsWith('{'))
                {
                    currentAssistantMessage.Append(e.Data);
                    currentAssistantMessage.AppendLine();
                    progress?.Report(new ExecutionProgress
                    {
                        CurrentMessage = e.Data.Length > 100 ? e.Data[..100] + "..." : e.Data,
                        IsStreaming = true
                    });
                }
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

        // Override success if a system-level error was detected during stream parsing
        // (CLI may exit with code 0 even when the upstream provider is unavailable)
        if (_systemErrorDetected)
        {
            result.Success = false;
            result.IsSystemError = true;
            result.ErrorMessage ??= _systemErrorMessage;
        }

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

    /// <summary>
    /// Builds the effective prompt by prepending system prompt content into the main prompt.
    /// Copilot CLI does not support --system-prompt or --append-system-prompt flags, so
    /// these instructions are folded into the prompt text using XML tags.
    /// </summary>
    private static string BuildEffectivePrompt(string prompt, string? systemPrompt, string? appendSystemPrompt)
    {
        var hasSystemPrompt = !string.IsNullOrEmpty(systemPrompt);
        var hasAppendSystemPrompt = !string.IsNullOrEmpty(appendSystemPrompt);

        if (!hasSystemPrompt && !hasAppendSystemPrompt)
        {
            return prompt;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<system-instructions>");

        if (hasSystemPrompt)
        {
            sb.AppendLine(systemPrompt);
        }

        if (hasSystemPrompt && hasAppendSystemPrompt)
        {
            sb.AppendLine();
        }

        if (hasAppendSystemPrompt)
        {
            sb.AppendLine(appendSystemPrompt);
        }

        sb.AppendLine("</system-instructions>");
        sb.AppendLine();
        sb.Append(prompt);

        return sb.ToString();
    }

    /// <summary>
    /// Parses usage information from Copilot CLI stderr output.
    /// Example format:
    /// [ERR]  claude-opus-4.5         49.0k in, 301 out, 32.0k cached (Est. 3 Premium requests)
    /// </summary>
    private void ParseCopilotUsageFromStderr(string stderr, ExecutionResult result)
    {
        CopilotUsageParser.ApplyToExecutionResult(stderr, result);
        _lastObservedUsageLimits = result.DetectedUsageLimits ?? _lastObservedUsageLimits;
    }

    private void ProcessStreamEvent(
        CopilotStreamEvent evt,
        ExecutionResult result,
        System.Text.StringBuilder currentMessage,
        IProgress<ExecutionProgress>? progress,
        Dictionary<string, string> toolNamesById)
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

        if (!string.IsNullOrEmpty(evt.Model))
        {
            result.ModelUsed = evt.Model;
        }

        ReportStructuredEvent(evt.ReasoningSummary, "reasoning_summary", result, progress, true);
        ReportStructuredEvent(evt.Reasoning, "reasoning", result, progress, true);
        ReportStructuredEvent(evt.Plan, "plan", result, progress, false);

        switch (evt.Type?.ToLowerInvariant())
        {
            case "system":
                // Claude Code format init event - session ID already captured above
                progress?.Report(new ExecutionProgress
                {
                    CurrentMessage = "Initializing...",
                    IsStreaming = false
                });
                break;

            case "message":
            case "response":
            case "assistant":
                // Check for root-level error on assistant events (Claude Code format:
                // model unavailable, upstream outages produce error: "invalid_request")
                if (!string.IsNullOrEmpty(evt.Error))
                {
                    _systemErrorDetected = true;
                    _systemErrorMessage = $"System error: {evt.Error}";

                    // Try to extract a more descriptive error from content blocks.
                    // Content can be at root level (evt.ParseContentBlocks) or inside
                    // the message object (evt.ParseClaudeMessage).
                    var rootContent = evt.ParseContentBlocks();
                    var msgContent = evt.ParseClaudeMessage()?.Content;
                    var contentBlocks = rootContent ?? msgContent;
                    if (contentBlocks != null)
                    {
                        var errorText = contentBlocks
                            .Where(c => c.Type == "text" && !string.IsNullOrEmpty(c.Text))
                            .Select(c => c.Text)
                            .FirstOrDefault();
                        if (errorText != null)
                            _systemErrorMessage = errorText;
                    }

                    result.Success = false;
                    result.ErrorMessage = _systemErrorMessage;
                    result.IsSystemError = true;
                    result.Messages.Add(new ExecutionMessage
                    {
                        Role = "system",
                        Content = $"[Error] {_systemErrorMessage}",
                        Timestamp = DateTime.UtcNow
                    });
                    progress?.Report(new ExecutionProgress
                    {
                        CurrentMessage = _systemErrorMessage,
                        IsStreaming = false
                    });
                    break;
                }

                // Extract usage and content from Claude-format message object
                var parsedMessage = evt.ParseClaudeMessage();
                if (parsedMessage != null)
                {
                    // Accumulate token usage from Claude-format assistant messages
                    if (parsedMessage.Usage != null)
                    {
                        if (parsedMessage.Usage.InputTokens.HasValue)
                        {
                            _accumulatedInputTokens += parsedMessage.Usage.InputTokens.Value;
                            _hasAccumulatedTokens = true;
                        }
                        if (parsedMessage.Usage.OutputTokens.HasValue)
                        {
                            _accumulatedOutputTokens += parsedMessage.Usage.OutputTokens.Value;
                            _hasAccumulatedTokens = true;
                        }
                    }

                    if (!string.IsNullOrEmpty(parsedMessage.Model))
                    {
                        result.ModelUsed = parsedMessage.Model;
                    }

                    // Extract text content from Claude-format message
                    if (parsedMessage.Content != null)
                    {
                        foreach (var content in parsedMessage.Content)
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
                                if (!string.IsNullOrWhiteSpace(content.Id) && !string.IsNullOrWhiteSpace(content.Name))
                                {
                                    toolNamesById[content.Id] = content.Name;
                                }
                                progress?.Report(new ExecutionProgress
                                {
                                    ToolName = content.Name,
                                    IsStreaming = false
                                });
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(evt.ContentText))
                {
                    // Native Copilot format - content is a direct string
                    currentMessage.Append(evt.ContentText);
                    progress?.Report(new ExecutionProgress
                    {
                        CurrentMessage = evt.ContentText.Length > 100
                            ? evt.ContentText[..100] + "..."
                            : evt.ContentText,
                        IsStreaming = true
                    });
                }
                break;

            case "user":
                // Claude Code format user events (tool results)
                var userMsg = evt.ParseClaudeMessage();
                if (userMsg?.Content != null)
                {
                    foreach (var content in userMsg.Content)
                    {
                        if (content.Type == "tool_result")
                        {
                            var resolvedToolName = !string.IsNullOrWhiteSpace(content.ToolUseId)
                                && toolNamesById.TryGetValue(content.ToolUseId, out var toolName)
                                ? toolName
                                : content.ToolUseId;
                            result.Messages.Add(new ExecutionMessage
                            {
                                Role = content.IsError == true ? "tool_error" : "tool_result",
                                Content = content.Content ?? "",
                                ToolName = resolvedToolName,
                                ToolOutput = content.Content,
                                Timestamp = DateTime.UtcNow
                            });
                        }
                    }
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
                    // Classify system-level errors (model unavailable, auth, upstream outages)
                    if (IsSystemLevelError(evt.Error))
                    {
                        _systemErrorDetected = true;
                        _systemErrorMessage = evt.Error;
                        result.IsSystemError = true;
                    }
                    result.Messages.Add(new ExecutionMessage
                    {
                        Role = "system",
                        Content = $"[Error] {evt.Error}",
                        Timestamp = DateTime.UtcNow
                    });
                    progress?.Report(new ExecutionProgress
                    {
                        CurrentMessage = $"Error: {evt.Error}",
                        IsStreaming = false
                    });
                }
                break;

            case "limit":
            case "rate_limit":
                var limitMessage = evt.MessageText ?? "Premium request limit reached";
                result.ErrorMessage = limitMessage;
                result.IsSystemError = true;
                _systemErrorDetected = true;
                _systemErrorMessage = limitMessage;
                result.DetectedUsageLimits = new UsageLimits
                {
                    LimitType = UsageLimitType.PremiumRequests,
                    IsLimitReached = true,
                    Message = limitMessage
                };
                _lastObservedUsageLimits = result.DetectedUsageLimits;
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
                else if (evt.TotalCostUsd.HasValue)
                {
                    result.CostUsd = evt.TotalCostUsd;
                }
                if (evt.Usage != null)
                {
                    result.InputTokens = evt.Usage.InputTokens ?? result.InputTokens;
                    result.OutputTokens = evt.Usage.OutputTokens ?? result.OutputTokens;
                }
                break;

            case "done":
            case "complete":
            case "result":
                // Check for system-level errors (Claude Code format: is_error with zero tokens)
                if (evt.IsError == true)
                {
                    _systemErrorDetected = true;
                    _systemErrorMessage ??= evt.Result ?? "Provider returned a system error";
                    result.Success = false;
                    result.ErrorMessage = _systemErrorMessage;
                    result.IsSystemError = true;
                    result.Messages.Add(new ExecutionMessage
                    {
                        Role = "system",
                        Content = $"[Error] {_systemErrorMessage}",
                        Timestamp = DateTime.UtcNow
                    });
                    progress?.Report(new ExecutionProgress
                    {
                        CurrentMessage = $"System error: {_systemErrorMessage}",
                        IsStreaming = false
                    });
                }

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
                    result.InputTokens = evt.Usage.InputTokens ?? result.InputTokens;
                    result.OutputTokens = evt.Usage.OutputTokens ?? result.OutputTokens;
                }
                // Fall back to flat token fields
                if (!result.InputTokens.HasValue && evt.InputTokens.HasValue)
                {
                    result.InputTokens = evt.InputTokens;
                }
                if (!result.OutputTokens.HasValue && evt.OutputTokens.HasValue)
                {
                    result.OutputTokens = evt.OutputTokens;
                }
                // Fall back to accumulated tokens from Claude-format assistant messages
                if (!result.InputTokens.HasValue && _hasAccumulatedTokens)
                {
                    result.InputTokens = _accumulatedInputTokens;
                }
                if (!result.OutputTokens.HasValue && _hasAccumulatedTokens)
                {
                    result.OutputTokens = _accumulatedOutputTokens;
                }
                if (!string.IsNullOrEmpty(evt.Result))
                {
                    result.Output = evt.Result;
                }
                break;
        }
    }

    private static void ReportStructuredEvent(
        string? content,
        string role,
        ExecutionResult result,
        IProgress<ExecutionProgress>? progress,
        bool isThinking)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        result.Messages.Add(new ExecutionMessage
        {
            Role = role,
            Content = content,
            Timestamp = DateTime.UtcNow
        });

        progress?.Report(new ExecutionProgress
        {
            CurrentMessage = content.Length > 100 ? content[..100] + "..." : content,
            IsStreaming = true,
            IsThinkingContent = isThinking,
            ContentCategory = isThinking ? "reasoning" : role
        });
    }

    /// <summary>
    /// Determines if an error message indicates a system-level issue (model unavailable,
    /// upstream outage, auth failure) rather than a task-level error.
    /// </summary>
    private static bool IsSystemLevelError(string error)
    {
        return error.Contains("model", StringComparison.OrdinalIgnoreCase) &&
                   (error.Contains("not exist", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("not have access", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("unavailable", StringComparison.OrdinalIgnoreCase)) ||
               error.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("upstream", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("service unavailable", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("internal server error", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("invalid_request", StringComparison.OrdinalIgnoreCase);
    }

    private CopilotClientOptions BuildMetadataClientOptions()
    {
        var options = new CopilotClientOptions
        {
            AutoStart = true,
            AutoRestart = false,
            UseStdio = true,
            LogLevel = "warning"
        };

        var cwd = WorkingDirectory ?? Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            options.Cwd = cwd;
        }

        if (!string.IsNullOrEmpty(ExecutablePath))
        {
            var resolvedPath = Path.IsPathRooted(ExecutablePath)
                ? ExecutablePath
                : Path.GetFullPath(ExecutablePath);

            if (File.Exists(resolvedPath))
            {
                options.CliPath = resolvedPath;
            }
        }

        return options;
    }

    /// <summary>
    /// Heuristic fallback for model multipliers when CLI detection is unavailable.
    /// Values are best-effort estimates; prefer CLI-discovered values when possible.
    /// </summary>
    internal static decimal GetDefaultModelMultiplier(string modelId)
    {
        var normalized = modelId.ToLowerInvariant();

        // Fast/preview variants of premium models (e.g., claude-opus-4.6-fast = 30x)
        if (normalized.Contains("fast") && normalized.Contains("opus"))
        {
            return 30.0m;
        }

        // Codex-max variants (e.g., gpt-5.1-codex-max = 1x)
        if (normalized.Contains("codex-max"))
        {
            return 1.0m;
        }

        // Codex LTS variants (e.g., gpt-5.3-codex-lts = 1x)
        if (normalized.Contains("codex-lts"))
        {
            return 1.0m;
        }

        // Opus models (3x premium requests)
        if (normalized.Contains("opus"))
        {
            return 3.0m;
        }

        // Free-tier models (0x - no premium request cost)
        if (normalized.Contains("gpt-4.1") || normalized.Contains("gpt-5-mini"))
        {
            return 0m;
        }

        // Mini/Haiku models (0.33x premium requests)
        // Use "-mini" to avoid matching "gemini" model family
        if (normalized.Contains("haiku") || normalized.Contains("-mini"))
        {
            return 0.33m;
        }

        // Default: 1x for standard models (sonnet, gpt-5.x, codex, gemini-pro, etc.)
        return 1.0m;
    }

    /// <summary>
    /// Discovers available models by triggering the CLI's --model validation error,
    /// which lists all valid model choices in the error message.
    /// </summary>
    private async Task<List<string>?> TryGetAvailableModelsFromCliAsync(CancellationToken cancellationToken)
    {
        var execPath = GetExecutablePath();
        if (string.IsNullOrEmpty(execPath))
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var startInfo = new ProcessStartInfo
            {
                FileName = execPath,
                Arguments = "--model __invalid_model_probe__"
            };

            PlatformHelper.ConfigureForCrossPlatform(startInfo);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // The error message with valid choices comes on stderr
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.StandardOutput.ReadToEndAsync(cts.Token);

            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } }

            return CopilotModelParser.ParseModelChoicesFromError(stderr);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<string>?> TryGetAvailableModelsAsync(CancellationToken cancellationToken)
    {
        CopilotClient? client = null;

        try
        {
            client = new CopilotClient(BuildMetadataClientOptions());
            await client.StartAsync(cancellationToken);

            var models = await client.ListModelsAsync(cancellationToken);
            return models?
                .Select(model => model.Id)
                .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (client != null)
            {
                try { await client.StopAsync(); } catch { }
                try { await client.DisposeAsync(); } catch { }
            }
        }
    }

    private async Task<string> ExecuteCliAsync(string prompt, CancellationToken cancellationToken)
    {
        var execPath = GetExecutablePath();
        if (string.IsNullOrEmpty(execPath))
        {
            throw new InvalidOperationException("GitHub Copilot CLI executable path is not configured.");
        }

        var args = $"-p \"{EscapeCliArgument(prompt)}\" --yolo --silent --autopilot";

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
            AvailableModels = new List<string>(),
            AvailableAgents = new List<AgentInfo>
            {
                new() { Name = "default", Description = "Default coding agent with full capabilities", IsDefault = true }
            },
            Pricing = new PricingInfo
            {
                Currency = "USD",
                ModelMultipliers = new Dictionary<string, decimal>()
            },
            AdditionalInfo = new Dictionary<string, object>
            {
                ["isAvailable"] = true,
                ["hasPremiumRequestLimit"] = true
            }
        };

        try
        {
            info.Version = await GetCliVersionAsync(GetExecutablePath(), "--binary-version", cancellationToken);

            // Prefer CLI-based model discovery (includes all models the CLI actually supports)
            var cliModels = await TryGetAvailableModelsFromCliAsync(cancellationToken);
            if (cliModels is { Count: > 0 })
            {
                info.AvailableModels = cliModels;
            }
            else
            {
                // Fall back to SDK-based discovery
                var sdkModels = await TryGetAvailableModelsAsync(cancellationToken);
                if (sdkModels is { Count: > 0 })
                {
                    info.AvailableModels = sdkModels;
                }
                else
                {
                    info.AdditionalInfo["modelsWarning"] = "Unable to query Copilot CLI for model metadata.";
                }
            }

            // Apply heuristic multipliers for discovered models
            foreach (var modelId in info.AvailableModels)
            {
                info.Pricing!.ModelMultipliers![modelId] = GetDefaultModelMultiplier(modelId);
            }

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
        if (_lastObservedUsageLimits != null)
        {
            return Task.FromResult(_lastObservedUsageLimits);
        }

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
