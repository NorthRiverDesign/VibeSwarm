using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using VibeSwarm.Shared.Providers.OpenCode;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Provider implementation for OpenCode CLI and REST API.
/// </summary>
public class OpenCodeProvider : CliProviderBase
{
    private readonly string? _apiEndpoint;
    private readonly string? _apiKey;
    private readonly HttpClient? _httpClient;

    private const string DefaultExecutable = "opencode";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public override ProviderType Type => ProviderType.OpenCode;

    public OpenCodeProvider(Provider config)
        : base(config.Id, config.Name, config.ConnectionMode, config.ExecutablePath, config.WorkingDirectory)
    {
        _apiEndpoint = config.ApiEndpoint;
        _apiKey = config.ApiKey;

        if (ConnectionMode == ProviderConnectionMode.REST && !string.IsNullOrEmpty(_apiEndpoint))
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_apiEndpoint),
                Timeout = TimeSpan.FromMinutes(30)
            };

            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            }
        }
    }

    private string GetExecutablePath() => ResolveExecutablePath(DefaultExecutable);

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (ConnectionMode == ProviderConnectionMode.CLI)
            {
                return await TestCliConnectionAsync(GetExecutablePath(), "OpenCode", cancellationToken: cancellationToken);
            }
            else
            {
                return await TestRestConnectionAsync(cancellationToken);
            }
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    private async Task<bool> TestRestConnectionAsync(CancellationToken cancellationToken)
    {
        if (_httpClient == null)
        {
            IsConnected = false;
            LastConnectionError = "REST API client is not configured. Check API endpoint and key settings.";
            return false;
        }

        try
        {
            var response = await _httpClient.GetAsync("/health", cancellationToken);
            IsConnected = response.IsSuccessStatusCode;

            if (!IsConnected)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                LastConnectionError = $"REST API test failed. Status: {(int)response.StatusCode} {response.ReasonPhrase}. " +
                    $"Endpoint: {_apiEndpoint}/health. Response: {responseBody}";
            }
            else
            {
                LastConnectionError = null;
            }

            return IsConnected;
        }
        catch (HttpRequestException ex)
        {
            IsConnected = false;
            LastConnectionError = $"Failed to connect to OpenCode API at {_apiEndpoint}: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastConnectionError = $"Unexpected error testing OpenCode REST API: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public override async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            return await ExecuteCliAsync(prompt, null, cancellationToken);
        }
        else
        {
            return await ExecuteRestAsync(prompt, cancellationToken);
        }
    }

    public override async Task<ExecutionResult> ExecuteWithSessionAsync(
        string prompt,
        string? sessionId = null,
        string? workingDirectory = null,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            return await ExecuteCliWithSessionAsync(prompt, sessionId, workingDirectory, progress, cancellationToken);
        }
        else
        {
            return await ExecuteRestWithSessionAsync(prompt, sessionId, progress, cancellationToken);
        }
    }

    /// <summary>
    /// Builds the CLI arguments for the OpenCode 'run' command.
    /// Reference: https://opencode.ai/docs/cli#run
    /// 
    /// Available flags for 'run' command:
    /// --command       The command to run, use message for args
    /// --continue, -c  Continue the last session
    /// --session, -s   Session ID to continue
    /// --share         Share the session
    /// --model, -m     Model to use in the form of provider/model
    /// --agent         Agent to use
    /// --file, -f      File(s) to attach to message
    /// --format        Format: default (formatted) or json (raw JSON events)
    /// --title         Title for the session
    /// --attach        Attach to a running opencode server
    /// --port          Port for the local server
    /// </summary>
    private List<string> BuildRunCommandArgs(string prompt, string? sessionId)
    {
        var args = new List<string> { "run" };

        // Session continuation options (--session takes precedence over --continue)
        if (!string.IsNullOrEmpty(sessionId))
        {
            args.AddRange(new[] { "--session", sessionId });
        }
        else if (CurrentContinueLastSession)
        {
            args.Add("--continue");
        }

        // Model selection (required for proper operation)
        // Priority: CurrentModel > Environment variable > None
        var model = CurrentModel;
        if (string.IsNullOrEmpty(model) && CurrentEnvironmentVariables != null)
        {
            CurrentEnvironmentVariables.TryGetValue("OPENCODE_MODEL", out model);
        }
        if (!string.IsNullOrEmpty(model))
        {
            args.AddRange(new[] { "--model", model });
        }

        // Agent selection
        if (!string.IsNullOrEmpty(CurrentAgent))
        {
            args.AddRange(new[] { "--agent", CurrentAgent });
        }

        // Session title
        if (!string.IsNullOrEmpty(CurrentTitle))
        {
            args.AddRange(new[] { "--title", $"\"{EscapeCliArgument(CurrentTitle)}\"" });
        }

        // Output format (default or json)
        if (!string.IsNullOrEmpty(CurrentOutputFormat))
        {
            args.AddRange(new[] { "--format", CurrentOutputFormat });
        }

        // File attachments
        if (CurrentAttachedFiles != null && CurrentAttachedFiles.Count > 0)
        {
            foreach (var file in CurrentAttachedFiles)
            {
                args.AddRange(new[] { "--file", $"\"{EscapeCliArgument(file)}\"" });
            }
        }

        // Additional custom arguments (provider-specific or user-defined)
        if (CurrentAdditionalArgs != null)
        {
            args.AddRange(CurrentAdditionalArgs);
        }

        // The prompt message must be the final argument
        args.Add($"\"{EscapeCliArgument(prompt)}\"");

        return args;
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
                ErrorMessage = "OpenCode executable path is not configured."
            };
        }

        var result = new ExecutionResult { Messages = new List<ExecutionMessage>() };
        var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;

        // Build the full command using the centralized argument builder
        // Reference: https://opencode.ai/docs/cli#run
        var args = BuildRunCommandArgs(prompt, sessionId);

        var fullArguments = string.Join(" ", args);
        var fullCommand = $"{execPath} {fullArguments}";

        using var process = CreateCliProcess(execPath, fullArguments, effectiveWorkingDir);

        var outputBuilder = new List<string>();
        var errorBuilder = new System.Text.StringBuilder();
        var currentAssistantMessage = new System.Text.StringBuilder();

        var outputComplete = new TaskCompletionSource<bool>();
        var errorComplete = new TaskCompletionSource<bool>();
        var earlyErrorDetected = false;

        // Track if we've received ANY meaningful output (from stdout OR stderr)
        // This is used to stop the "waiting for response" messages once the CLI starts working
        var hasReceivedMeaningfulOutput = false;
        var outputLock = new object();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                outputComplete.TrySetResult(true);
                return;
            }

            if (string.IsNullOrEmpty(e.Data)) return;

            // Strip ANSI codes for storage and parsing
            var cleanedData = StripAnsiCodes(e.Data);

            lock (outputLock)
            {
                outputBuilder.Add(cleanedData);
                hasReceivedMeaningfulOutput = true;
            }

            // Check for early error patterns in the output
            // This allows us to detect errors like "model 'x' not found" immediately
            if (!earlyErrorDetected && IsCliErrorLine(cleanedData))
            {
                earlyErrorDetected = true;
                result.Success = false;
                progress?.Report(new ExecutionProgress
                {
                    OutputLine = cleanedData,
                    IsErrorOutput = true,
                    IsStreaming = true,
                    CurrentMessage = $"CLI Error: {cleanedData}"
                });
                return;
            }

            // Try to parse as JSON first
            try
            {
                var jsonEvent = JsonSerializer.Deserialize<OpenCodeStreamEvent>(cleanedData, JsonOptions);
                if (jsonEvent != null && !string.IsNullOrEmpty(jsonEvent.Type))
                {
                    ProcessStreamEvent(jsonEvent, result, currentAssistantMessage, progress);
                    return;
                }
            }
            catch
            {
                // Not JSON, continue with plain text handling
            }

            // Plain text output
            currentAssistantMessage.AppendLine(cleanedData);
            progress?.Report(new ExecutionProgress
            {
                OutputLine = cleanedData,
                IsStreaming = true
            });
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
                // Strip ANSI codes from stderr as well
                var cleanedError = StripAnsiCodes(e.Data);
                errorBuilder.AppendLine(cleanedError);

                // OpenCode outputs tool progress (Read, Edit, Write, etc.) to stderr
                // These are NOT errors - they're progress indicators
                var isToolProgress = IsOpenCodeToolProgress(cleanedError);

                // Only mark as error if it's a real CLI error, not tool progress
                if (!earlyErrorDetected && !isToolProgress && IsCliErrorLine(cleanedError))
                {
                    earlyErrorDetected = true;
                    result.Success = false;
                }

                // Mark that we've received meaningful output (tool progress counts!)
                lock (outputLock)
                {
                    hasReceivedMeaningfulOutput = true;
                }

                // Report with appropriate labeling
                if (isToolProgress)
                {
                    // Tool progress - show as normal output with tool indicator
                    progress?.Report(new ExecutionProgress
                    {
                        OutputLine = cleanedError,
                        IsErrorOutput = false, // Don't mark tool progress as error
                        IsStreaming = true,
                        ToolName = ExtractToolNameFromProgress(cleanedError)
                    });
                }
                else
                {
                    // Actual stderr content
                    progress?.Report(new ExecutionProgress
                    {
                        OutputLine = cleanedError,
                        IsErrorOutput = true,
                        IsStreaming = true,
                        CurrentMessage = earlyErrorDetected ? $"CLI Error: {cleanedError}" : null
                    });
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ExecutionResult
            {
                Success = false,
                ErrorMessage = $"Failed to start OpenCode CLI process: {ex.Message}. " +
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

        // Close stdin to signal the CLI that no interactive input is coming.
        // Many CLI tools (including OpenCode) wait for stdin to close before processing.
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // Ignore if stdin is already closed
        }

        result.ProcessId = process.Id;
        result.CommandUsed = fullCommand;
        ReportProcessStarted(process.Id, progress, fullCommand);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Start initialization monitor - checks BOTH stdout and stderr for output
        using var initMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var initializationMonitorTask = CreateInitializationMonitorAsync(
            () => { lock (outputLock) { return hasReceivedMeaningfulOutput; } },
            progress,
            initMonitorCts.Token);

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

        if (process.ExitCode != 0)
        {
            result.Success = false;
        }

        // Strip ANSI codes from output for cleaner parsing
        List<string> cleanedOutput;
        lock (outputLock)
        {
            cleanedOutput = outputBuilder.Select(StripAnsiCodes).ToList();
        }
        result.Output = string.Join("\n", cleanedOutput);

        var stderrContent = StripAnsiCodes(errorBuilder.ToString());

        // Check for errors in output even if exit code was 0
        // Some CLIs output errors but still return 0
        var errorFromOutput = OpenCodeOutputParser.ExtractErrorFromOutput(cleanedOutput);
        var errorFromStderr = !string.IsNullOrWhiteSpace(stderrContent)
            ? OpenCodeOutputParser.ExtractErrorFromOutput(stderrContent.Split('\n').ToList())
            : null;

        // Only mark as failed if we found actual error patterns (not just tool progress in stderr)
        // Exit code 0 with only tool progress in stderr is a SUCCESS
        if (!string.IsNullOrWhiteSpace(errorFromOutput) || !string.IsNullOrWhiteSpace(errorFromStderr))
        {
            result.Success = false;
        }

        // If exit code is 0 and no error patterns were found, ensure success is true
        // even if there's content in stderr (likely just tool progress output)
        if (process.ExitCode == 0 && string.IsNullOrWhiteSpace(errorFromOutput) && string.IsNullOrWhiteSpace(errorFromStderr))
        {
            result.Success = true;
        }

        if (!result.Success)
        {
            var errorMessages = new List<string>();

            // Only include stderr in error if it contains actual errors, not just tool progress
            if (!string.IsNullOrWhiteSpace(errorFromStderr))
            {
                // Include the actual error content, not the full stderr
                errorMessages.Add($"[stderr] {errorFromStderr.Trim()}");
            }
            else if (!string.IsNullOrWhiteSpace(stderrContent) && process.ExitCode != 0)
            {
                // Include full stderr only if exit code indicates failure
                // Filter out tool progress lines for cleaner error display
                var stderrLines = stderrContent.Split('\n')
                    .Where(line => !OpenCodeOutputParser.IsToolProgressLine(line))
                    .ToList();
                var filteredStderr = string.Join("\n", stderrLines).Trim();
                if (!string.IsNullOrWhiteSpace(filteredStderr))
                {
                    errorMessages.Add($"[stderr] {filteredStderr}");
                }
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                errorMessages.Add(result.ErrorMessage);
            }

            if (!string.IsNullOrWhiteSpace(errorFromOutput))
            {
                errorMessages.Add(errorFromOutput);
            }

            if (errorMessages.Count == 0 && cleanedOutput.Count > 0)
            {
                var lastLines = cleanedOutput.TakeLast(10).ToList();
                var contextOutput = string.Join("\n", lastLines);
                if (!string.IsNullOrWhiteSpace(contextOutput))
                {
                    errorMessages.Add($"Last output:\n{contextOutput}");
                }
            }

            if (errorMessages.Count == 0)
            {
                errorMessages.Add($"OpenCode CLI exited with code {process.ExitCode}. Check the console output for details.");
            }
            else
            {
                errorMessages.Insert(0, $"OpenCode CLI exited with code {process.ExitCode}:");
            }

            result.ErrorMessage = string.Join("\n", errorMessages);
        }

        return result;
    }

    /// <summary>
    /// Strips ANSI escape codes from a string for cleaner parsing.
    /// </summary>
    private static string StripAnsiCodes(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Pattern matches ANSI escape sequences: ESC [ ... (letter or ~)
        return System.Text.RegularExpressions.Regex.Replace(
            input,
            @"\x1B\[[0-9;]*[a-zA-Z~]|\x1B\].*?\x07|\x1B[PX^_].*?\x1B\\|\x1B.?",
            string.Empty);
    }

    /// <summary>
    /// Determines if a line represents a CLI error that should cause immediate failure.
    /// This helps detect errors early in the streaming output.
    /// </summary>
    private static bool IsCliErrorLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var lowerLine = line.Trim().ToLowerInvariant();

        // Common CLI error patterns
        return lowerLine.StartsWith("error:") ||
               lowerLine.StartsWith("error ") ||
               lowerLine.StartsWith("fatal:") ||
               lowerLine.StartsWith("fatal ") ||
               lowerLine.StartsWith("panic:") ||
               // Model not found errors (e.g., "Error: model 'kimi-k2.5' not found")
               (lowerLine.Contains("model") && lowerLine.Contains("not found")) ||
               // API key errors
               (lowerLine.Contains("api") && (lowerLine.Contains("key") || lowerLine.Contains("invalid") || lowerLine.Contains("missing"))) ||
               // Authentication errors
               (lowerLine.Contains("auth") && (lowerLine.Contains("failed") || lowerLine.Contains("error") || lowerLine.Contains("invalid"))) ||
               // Connection errors
               (lowerLine.Contains("connection") && (lowerLine.Contains("refused") || lowerLine.Contains("failed") || lowerLine.Contains("timeout"))) ||
               // Provider errors
               (lowerLine.Contains("provider") && (lowerLine.Contains("not found") || lowerLine.Contains("unavailable") || lowerLine.Contains("error"))) ||
               // Permission errors
               lowerLine.Contains("permission denied") ||
               lowerLine.Contains("access denied") ||
               // Generic not found that starts the line
               lowerLine.StartsWith("not found:") ||
               // Rate limiting
               lowerLine.Contains("rate limit") ||
               lowerLine.Contains("quota exceeded");
    }

    /// <summary>
    /// Determines if a stderr line is OpenCode tool progress output, not an actual error.
    /// OpenCode outputs tool actions (Read, Edit, Write, Bash, Glob, etc.) to stderr as progress indicators.
    /// These should be displayed to the user but NOT treated as errors.
    /// </summary>
    private static bool IsOpenCodeToolProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.Trim();

        // OpenCode tool progress format: "|  ToolName    path/to/file" or similar
        // Common tools: Read, Write, Edit, Bash, Glob, Grep, LS, TodoRead, TodoWrite, etc.

        // Check for the pipe character pattern that OpenCode uses
        if (trimmed.StartsWith("|"))
        {
            var afterPipe = trimmed.TrimStart('|').TrimStart();

            // Common OpenCode tool names
            var toolKeywords = new[]
            {
                "Read", "Write", "Edit", "Bash", "Glob", "Grep", "LS", "List",
                "Todo", "TodoRead", "TodoWrite", "Fetch", "Search", "Find",
                "MultiEdit", "Patch", "View", "Cat", "Head", "Tail",
                "Mkdir", "Rm", "Mv", "Cp", "Touch", "Chmod"
            };

            foreach (var tool in toolKeywords)
            {
                if (afterPipe.StartsWith(tool, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Also check for thinking/reasoning indicators
        if (trimmed.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Planning", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Analyzing", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts the tool name from an OpenCode progress line.
    /// </summary>
    private static string? ExtractToolNameFromProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();

        if (trimmed.StartsWith("|"))
        {
            var afterPipe = trimmed.TrimStart('|').TrimStart();

            // Extract the first word as the tool name
            var spaceIndex = afterPipe.IndexOf(' ');
            if (spaceIndex > 0)
            {
                return afterPipe[..spaceIndex];
            }
            else if (afterPipe.Length > 0)
            {
                return afterPipe;
            }
        }

        return null;
    }

    private void ProcessStreamEvent(
        OpenCodeStreamEvent evt,
        ExecutionResult result,
        System.Text.StringBuilder currentMessage,
        IProgress<ExecutionProgress>? progress)
    {
        switch (evt.Type)
        {
            case "session":
                if (!string.IsNullOrEmpty(evt.SessionId))
                {
                    result.SessionId = evt.SessionId;
                }
                break;

            case "message":
            case "assistant":
                if (!string.IsNullOrEmpty(evt.Content))
                {
                    currentMessage.Append(evt.Content);
                    progress?.Report(new ExecutionProgress
                    {
                        CurrentMessage = evt.Content,
                        IsStreaming = true
                    });
                }
                break;

            case "tool_call":
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
                    Content = evt.ToolName ?? "unknown",
                    ToolName = evt.ToolName,
                    ToolInput = evt.ToolInput,
                    Timestamp = DateTime.UtcNow
                });
                progress?.Report(new ExecutionProgress
                {
                    ToolName = evt.ToolName,
                    IsStreaming = false
                });
                break;

            case "tool_result":
                result.Messages.Add(new ExecutionMessage
                {
                    Role = "tool_result",
                    Content = evt.ToolOutput ?? "",
                    ToolName = evt.ToolName,
                    ToolOutput = evt.ToolOutput,
                    Timestamp = DateTime.UtcNow
                });
                break;

            case "done":
            case "complete":
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
                if (!string.IsNullOrEmpty(evt.Model))
                {
                    result.ModelUsed = evt.Model;
                }
                break;

            case "error":
                var errorContent = evt.Error ?? evt.Message ?? evt.Content ?? "Unknown error";
                result.Success = false;
                result.ErrorMessage = string.IsNullOrEmpty(result.ErrorMessage)
                    ? errorContent
                    : $"{result.ErrorMessage}\n{errorContent}";

                result.Messages.Add(new ExecutionMessage
                {
                    Role = "error",
                    Content = errorContent,
                    Timestamp = DateTime.UtcNow
                });

                progress?.Report(new ExecutionProgress
                {
                    CurrentMessage = $"Error: {errorContent}",
                    IsStreaming = false,
                    IsErrorOutput = true
                });
                break;

            default:
                if (!string.IsNullOrEmpty(evt.Error))
                {
                    result.ErrorMessage = string.IsNullOrEmpty(result.ErrorMessage)
                        ? evt.Error
                        : $"{result.ErrorMessage}\n{evt.Error}";
                }
                break;
        }
    }

    private async Task<ExecutionResult> ExecuteRestWithSessionAsync(
        string prompt,
        string? sessionId,
        IProgress<ExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_httpClient == null)
        {
            return new ExecutionResult
            {
                Success = false,
                ErrorMessage = "REST client is not configured."
            };
        }

        var result = new ExecutionResult { Messages = new List<ExecutionMessage>() };

        result.Messages.Add(new ExecutionMessage
        {
            Role = "user",
            Content = prompt,
            Timestamp = DateTime.UtcNow
        });

        var request = new
        {
            prompt,
            session_id = sessionId
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/run", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var apiResult = await response.Content.ReadFromJsonAsync<OpenCodeApiResponse>(JsonOptions, cancellationToken);

            if (apiResult != null)
            {
                result.SessionId = apiResult.SessionId;
                result.InputTokens = apiResult.InputTokens;
                result.OutputTokens = apiResult.OutputTokens;
                result.CostUsd = apiResult.CostUsd;

                result.Messages.Add(new ExecutionMessage
                {
                    Role = "assistant",
                    Content = apiResult.Output ?? "",
                    Timestamp = DateTime.UtcNow
                });

                result.Output = apiResult.Output;
                result.Success = apiResult.Success;

                if (!apiResult.Success && !string.IsNullOrEmpty(apiResult.Error))
                {
                    result.ErrorMessage = apiResult.Error;
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<string> ExecuteCliAsync(string prompt, string? sessionId, CancellationToken cancellationToken)
    {
        var execPath = GetExecutablePath();
        if (string.IsNullOrEmpty(execPath))
        {
            throw new InvalidOperationException("OpenCode executable path is not configured.");
        }

        // Build arguments using the centralized command builder
        // Reference: https://opencode.ai/docs/cli#run
        var argsList = BuildRunCommandArgs(prompt, sessionId);

        var startInfo = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = string.Join(" ", argsList)
        };

        PlatformHelper.ConfigureForCrossPlatform(startInfo);

        if (!string.IsNullOrEmpty(WorkingDirectory))
        {
            startInfo.WorkingDirectory = WorkingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Close stdin to signal the CLI that no interactive input is coming.
        // Many CLI tools (including OpenCode) wait for stdin to close before processing.
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // Ignore if stdin is already closed
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
        {
            throw new InvalidOperationException($"OpenCode execution failed: {error}");
        }

        return output;
    }

    private async Task<string> ExecuteRestAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_httpClient == null)
        {
            throw new InvalidOperationException("REST client is not configured.");
        }

        var request = new { prompt };
        var response = await _httpClient.PostAsJsonAsync("/run", request, cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenCodeApiResponse>(JsonOptions, cancellationToken);
        return result?.Output ?? string.Empty;
    }

    public override async Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default)
    {
        var defaultModels = new List<string>
        {
            "anthropic/claude-sonnet-4-20250514",
            "anthropic/claude-opus-4-20250514",
            "openai/gpt-4o",
            "openai/o1"
        };

        var info = new ProviderInfo
        {
            AvailableModels = defaultModels,
            AvailableAgents = new List<AgentInfo>
            {
                new() { Name = "build", Description = "Default, full access agent for development work", IsDefault = true },
                new() { Name = "plan", Description = "Read-only agent for analysis and code exploration", IsDefault = false }
            },
            Pricing = new PricingInfo
            {
                Currency = "USD",
                ModelMultipliers = new Dictionary<string, decimal>
                {
                    ["anthropic/claude-sonnet-4-20250514"] = 1.0m,
                    ["anthropic/claude-opus-4-20250514"] = 5.0m,
                    ["openai/gpt-4o"] = 0.83m,
                    ["openai/o1"] = 5.0m
                }
            },
            AdditionalInfo = new Dictionary<string, object>
            {
                ["isAvailable"] = true
            }
        };

        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            var execPath = GetExecutablePath();

            // Fetch available models dynamically
            try
            {
                using var modelsTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var modelsLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, modelsTimeoutCts.Token);

                var modelsStartInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "models"
                };

                PlatformHelper.ConfigureForCrossPlatform(modelsStartInfo);

                using var modelsProcess = new Process { StartInfo = modelsStartInfo };
                modelsProcess.Start();

                try
                {
                    var modelsOutput = await modelsProcess.StandardOutput.ReadToEndAsync(modelsLinkedCts.Token);
                    await modelsProcess.WaitForExitAsync(modelsLinkedCts.Token);

                    if (modelsProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(modelsOutput))
                    {
                        var models = OpenCodeOutputParser.ParseModelsOutput(modelsOutput);
                        if (models.Count > 0)
                        {
                            info.AvailableModels = models;

                            foreach (var model in models)
                            {
                                if (!info.Pricing.ModelMultipliers.ContainsKey(model))
                                {
                                    info.Pricing.ModelMultipliers[model] = OpenCodeOutputParser.GetDefaultModelMultiplier(model);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    try { modelsProcess.Kill(entireProcessTree: true); } catch { }
                    info.AdditionalInfo["modelsWarning"] = "Timed out while fetching available models, using defaults";
                }
            }
            catch (Exception ex)
            {
                info.AdditionalInfo["modelsWarning"] = $"Failed to fetch models: {ex.Message}, using defaults";
            }

            info.Version = await GetCliVersionAsync(execPath, cancellationToken: cancellationToken);
        }

        return info;
    }

    public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
    {
        // OpenCode does not have built-in limits - it depends on the underlying model
        var limits = new UsageLimits
        {
            LimitType = UsageLimitType.None,
            IsLimitReached = false,
            Message = "No built-in limits. Usage depends on the underlying model provider's API limits and quotas."
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

                // Try: opencode session show <id>
                var args = $"session show {sessionId} --format json";

                var startInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = args,
                    WorkingDirectory = effectiveWorkingDir
                };

                PlatformHelper.ConfigureForCrossPlatform(startInfo);

                using var process = new Process { StartInfo = startInfo };
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    process.Start();
                    process.StandardInput.Close();

                    var output = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                    await process.WaitForExitAsync(linkedCts.Token);

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        var sessionSummary = OpenCodeOutputParser.ParseSessionOutput(output);
                        if (!string.IsNullOrWhiteSpace(sessionSummary))
                        {
                            summary.Success = true;
                            summary.Summary = sessionSummary;
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

    public override async Task<PromptResponse> GetPromptResponseAsync(
        string prompt,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            var execPath = GetExecutablePath();
            if (string.IsNullOrEmpty(execPath))
            {
                return PromptResponse.Fail("OpenCode executable path is not configured.");
            }

            // Build arguments for simple prompt execution
            // Reference: https://opencode.ai/docs/cli#run
            var argsList = new List<string> { "run" };

            // Add model if specified
            if (!string.IsNullOrEmpty(CurrentModel))
            {
                argsList.AddRange(new[] { "--model", CurrentModel });
            }

            // Add the prompt message
            argsList.Add($"\"{EscapeCliArgument(prompt)}\"");

            var args = string.Join(" ", argsList);
            return await ExecuteSimplePromptAsync(execPath, args, "OpenCode", workingDirectory, cancellationToken);
        }
        else
        {
            if (_httpClient == null)
            {
                return PromptResponse.Fail("REST API client is not configured.");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var request = new
                {
                    prompt = prompt,
                    max_tokens = 4096
                };

                var response = await _httpClient.PostAsJsonAsync("/v1/prompt", request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<OpenCodeApiResponse>(JsonOptions, cancellationToken);

                stopwatch.Stop(); ;

                if (result?.Success == true && !string.IsNullOrEmpty(result.Output))
                {
                    return PromptResponse.Ok(result.Output, stopwatch.ElapsedMilliseconds, "opencode");
                }

                return PromptResponse.Fail(result?.Error ?? "No response content received from OpenCode API.");
            }
            catch (HttpRequestException ex)
            {
                return PromptResponse.Fail($"HTTP error calling OpenCode API: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                return PromptResponse.Fail("Request was cancelled.");
            }
            catch (Exception ex)
            {
                return PromptResponse.Fail($"Error calling OpenCode API: {ex.Message}");
            }
        }
    }
}
