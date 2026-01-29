using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

public class OpenCodeProvider : ProviderBase
{
    private readonly string? _executablePath;
    private readonly string? _workingDirectory;
    private readonly string? _apiEndpoint;
    private readonly string? _apiKey;
    private readonly HttpClient? _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public override ProviderType Type => ProviderType.OpenCode;

    public OpenCodeProvider(Provider config)
        : base(config.Id, config.Name, config.ConnectionMode)
    {
        _executablePath = config.ExecutablePath;
        _workingDirectory = config.WorkingDirectory;
        _apiEndpoint = config.ApiEndpoint;
        _apiKey = config.ApiKey;

        if (ConnectionMode == ProviderConnectionMode.REST && !string.IsNullOrEmpty(_apiEndpoint))
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_apiEndpoint),
                // Set a long timeout for agent tasks that may take several minutes
                Timeout = TimeSpan.FromMinutes(30)
            };

            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            }
        }
    }

    public override async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (ConnectionMode == ProviderConnectionMode.CLI)
            {
                return await TestCliConnectionAsync(cancellationToken);
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

    private async Task<bool> TestCliConnectionAsync(CancellationToken cancellationToken)
    {
        var execPath = GetExecutablePath();
        if (string.IsNullOrEmpty(execPath))
        {
            IsConnected = false;
            LastConnectionError = "OpenCode executable path is not configured.";
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

            // Use a timeout to prevent hanging on CLI calls
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            process.Start();

            // Close stdin immediately to signal no user input is available
            // This prevents the CLI from hanging if it tries to read input
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
                // Timeout occurred - kill the process
                try { process.Kill(entireProcessTree: true); } catch { }
                IsConnected = false;
                LastConnectionError = $"CLI test timed out after 10 seconds. Command: {execPath} version";
                return false;
            }

            // Read output for additional validation
            var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);

            // Check if the CLI is accessible and functioning
            IsConnected = process.ExitCode == 0 && !string.IsNullOrEmpty(output);

            if (!IsConnected)
            {
                var errorDetails = new System.Text.StringBuilder();
                errorDetails.AppendLine($"CLI test failed for command: {execPath} version");
                errorDetails.AppendLine($"Exit code: {process.ExitCode}");

                if (!string.IsNullOrEmpty(error))
                {
                    errorDetails.AppendLine($"Error output: {error.Trim()}");
                }

                if (string.IsNullOrEmpty(output))
                {
                    errorDetails.AppendLine("No output received from version command.");
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
            // Executable not found or can't be started
            IsConnected = false;
            var envPath = Environment.GetEnvironmentVariable("PATH") ?? "not set";
            LastConnectionError = $"Failed to start OpenCode CLI: {ex.Message}. " +
                $"Executable path: '{execPath}'. " +
                $"Current PATH: {envPath}. " +
                $"If running as a systemd service, ensure the executable is in a standard location " +
                $"or configure the full path to the executable in the provider settings.";
            return false;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastConnectionError = $"Unexpected error testing OpenCode CLI connection: {ex.GetType().Name}: {ex.Message}";
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
        var effectiveWorkingDir = workingDirectory ?? _workingDirectory ?? Environment.CurrentDirectory;

        // Build arguments for opencode run command
        // OpenCode v1.1.x uses: opencode run [message..] with options
        var args = new List<string> { "run" };

        // Session continuation uses -s or --session
        if (!string.IsNullOrEmpty(sessionId))
        {
            args.AddRange(new[] { "-s", sessionId });
        }

        // Add model if specified in environment variables
        if (CurrentEnvironmentVariables != null &&
            CurrentEnvironmentVariables.TryGetValue("OPENCODE_MODEL", out var model) &&
            !string.IsNullOrEmpty(model))
        {
            args.AddRange(new[] { "-m", model });
        }

        // Add any additional CLI arguments
        if (CurrentAdditionalArgs != null)
        {
            args.AddRange(CurrentAdditionalArgs);
        }

        // Add the prompt at the end (the message for the run command)
        args.Add("--");
        args.Add(EscapeArgument(prompt));

        var startInfo = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = string.Join(" ", args),
            WorkingDirectory = effectiveWorkingDir
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

        // Use TaskCompletionSource to ensure all output is processed before continuing
        var outputComplete = new TaskCompletionSource<bool>();
        var errorComplete = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                // End of stream
                outputComplete.TrySetResult(true);
                return;
            }

            if (string.IsNullOrEmpty(e.Data)) return;

            outputBuilder.Add(e.Data);

            // Try to parse as JSON first (in case format is json)
            try
            {
                var jsonEvent = JsonSerializer.Deserialize<OpenCodeStreamEvent>(e.Data, JsonOptions);
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

            // Plain text output - append to current message and report progress
            currentAssistantMessage.AppendLine(e.Data);

            // Report output line for live streaming
            progress?.Report(new ExecutionProgress
            {
                OutputLine = e.Data,
                IsStreaming = true
            });
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                // End of stream
                errorComplete.TrySetResult(true);
                return;
            }

            if (!string.IsNullOrEmpty(e.Data))
            {
                errorBuilder.AppendLine(e.Data);

                // Also report stderr as output for visibility
                progress?.Report(new ExecutionProgress
                {
                    OutputLine = e.Data,
                    IsErrorOutput = true,
                    IsStreaming = true
                });
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

        // Capture process ID immediately after starting
        result.ProcessId = process.Id;

        // Report process ID through progress callback for immediate tracking
        progress?.Report(new ExecutionProgress
        {
            CurrentMessage = "CLI process started successfully",
            ProcessId = process.Id,
            IsStreaming = false
        });

        // Report startup as output line for visibility in live output
        progress?.Report(new ExecutionProgress
        {
            OutputLine = $"[VibeSwarm] Process started (PID: {process.Id}). Waiting for CLI to initialize...",
            IsStreaming = true
        });

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Track when we last received output to detect initialization delays
        var lastOutputTime = DateTime.UtcNow;
        var outputReceived = false;
        var initializationWarningsSent = 0;
        var initializationCheckInterval = TimeSpan.FromSeconds(5);

        // Monitor for initialization delays - report progress while waiting for first output
        using var initMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var initializationMonitorTask = Task.Run(async () =>
        {
            try
            {
                while (!initMonitorCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(initializationCheckInterval, initMonitorCts.Token);

                    // Check if we've received any output yet
                    lock (outputBuilder)
                    {
                        if (outputBuilder.Count > 0)
                        {
                            outputReceived = true;
                        }
                    }

                    if (outputReceived)
                    {
                        // Output has started flowing, stop the initialization monitor
                        break;
                    }

                    var waitTime = DateTime.UtcNow - lastOutputTime;
                    initializationWarningsSent++;

                    // Report waiting status as output line for visibility
                    var waitMessage = initializationWarningsSent switch
                    {
                        1 => $"[VibeSwarm] Still initializing... (waited {waitTime.TotalSeconds:F0}s). The model may be loading.",
                        2 => $"[VibeSwarm] Still waiting for response... (waited {waitTime.TotalSeconds:F0}s). Ollama models can take time to load.",
                        3 => $"[VibeSwarm] Initialization taking longer than expected ({waitTime.TotalSeconds:F0}s). Please be patient.",
                        _ => $"[VibeSwarm] Still waiting ({waitTime.TotalSeconds:F0}s)... Process is running but no output yet."
                    };

                    progress?.Report(new ExecutionProgress
                    {
                        OutputLine = waitMessage,
                        IsStreaming = true
                    });

                    // Also update activity message
                    progress?.Report(new ExecutionProgress
                    {
                        CurrentMessage = $"Waiting for CLI response ({waitTime.TotalSeconds:F0}s)...",
                        IsStreaming = false
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when process completes or is cancelled
            }
        }, initMonitorCts.Token);

        // Wait for the process to exit - this can take several minutes for complex agent tasks
        try
        {
            await process.WaitForExitAsync(cancellationToken);

            // Stop the initialization monitor
            initMonitorCts.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Stop the initialization monitor
            initMonitorCts.Cancel();
            // If cancelled, try to kill the process
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        // Ensure initialization monitor task completes
        try { await initializationMonitorTask; } catch (OperationCanceledException) { }

        // Wait for all output to be processed (with timeout to prevent hanging)
        var outputTimeout = Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.WhenAny(Task.WhenAll(outputComplete.Task, errorComplete.Task), outputTimeout);

        // Finalize any pending assistant message
        if (currentAssistantMessage.Length > 0)
        {
            result.Messages.Add(new ExecutionMessage
            {
                Role = "assistant",
                Content = currentAssistantMessage.ToString(),
                Timestamp = DateTime.UtcNow
            });
        }

        // Determine success based on exit code (may have been set to false by error events)
        if (process.ExitCode != 0)
        {
            result.Success = false;
        }
        result.Output = string.Join("\n", outputBuilder);

        var stderrError = errorBuilder.ToString();

        // Build a comprehensive error message when the process failed
        if (!result.Success)
        {
            var errorMessages = new List<string>();

            // Add stderr if present
            if (!string.IsNullOrWhiteSpace(stderrError))
            {
                errorMessages.Add($"[stderr] {stderrError.Trim()}");
            }

            // Add any error captured from stream events
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                errorMessages.Add(result.ErrorMessage);
            }

            // Always try to extract error information from stdout
            var errorFromOutput = ExtractErrorFromOutput(outputBuilder);
            if (!string.IsNullOrWhiteSpace(errorFromOutput))
            {
                errorMessages.Add(errorFromOutput);
            }

            // If still no meaningful error, include the last few lines of output as context
            if (errorMessages.Count == 0 && outputBuilder.Count > 0)
            {
                var lastLines = outputBuilder.TakeLast(10).ToList();
                var contextOutput = string.Join("\n", lastLines);
                if (!string.IsNullOrWhiteSpace(contextOutput))
                {
                    errorMessages.Add($"Last output:\n{contextOutput}");
                }
            }

            // Always provide exit code context
            if (errorMessages.Count == 0)
            {
                errorMessages.Add($"OpenCode CLI exited with code {process.ExitCode}. Check the console output for details.");
            }
            else
            {
                // Prepend exit code info for context
                errorMessages.Insert(0, $"OpenCode CLI exited with code {process.ExitCode}:");
            }

            result.ErrorMessage = string.Join("\n", errorMessages);
        }

        return result;
    }

    /// <summary>
    /// Attempts to extract meaningful error information from CLI output
    /// </summary>
    private static string? ExtractErrorFromOutput(List<string> outputLines)
    {
        var errorLines = new List<string>();

        foreach (var line in outputLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Try to parse as JSON and look for error fields
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Check for error type events
                if (root.TryGetProperty("type", out var typeProp))
                {
                    var eventType = typeProp.GetString()?.ToLowerInvariant();
                    if (eventType == "error" || eventType == "fatal" || eventType == "panic")
                    {
                        // Extract error details from various possible fields
                        var errorText = GetJsonStringProperty(root, "error")
                            ?? GetJsonStringProperty(root, "message")
                            ?? GetJsonStringProperty(root, "content")
                            ?? GetJsonStringProperty(root, "detail")
                            ?? GetJsonStringProperty(root, "reason");

                        if (!string.IsNullOrWhiteSpace(errorText) && !errorLines.Contains(errorText))
                        {
                            errorLines.Add(errorText);
                        }
                    }
                }

                // Check for error field on any event type
                var anyError = GetJsonStringProperty(root, "error");
                if (!string.IsNullOrWhiteSpace(anyError) && !errorLines.Contains(anyError))
                {
                    errorLines.Add(anyError);
                }

                // Check for common error indicator fields
                var errorMsg = GetJsonStringProperty(root, "error_message")
                    ?? GetJsonStringProperty(root, "errorMessage")
                    ?? GetJsonStringProperty(root, "err");
                if (!string.IsNullOrWhiteSpace(errorMsg) && !errorLines.Contains(errorMsg))
                {
                    errorLines.Add(errorMsg);
                }
            }
            catch
            {
                // Not JSON - check for common error patterns in plain text
                var trimmedLine = line.Trim();
                var lowerLine = trimmedLine.ToLowerInvariant();

                // Check for explicit error indicators
                if (lowerLine.StartsWith("error:") ||
                    lowerLine.StartsWith("error ") ||
                    lowerLine.StartsWith("failed:") ||
                    lowerLine.StartsWith("fatal:") ||
                    lowerLine.StartsWith("panic:") ||
                    lowerLine.StartsWith("exception:") ||
                    lowerLine.Contains("error:") ||
                    lowerLine.Contains("failed to") ||
                    lowerLine.Contains("cannot ") ||
                    lowerLine.Contains("unable to") ||
                    lowerLine.Contains("not found") ||
                    lowerLine.Contains("invalid ") ||
                    lowerLine.Contains("no such") ||
                    (lowerLine.Contains("error") && (lowerLine.Contains("model") || lowerLine.Contains("api") || lowerLine.Contains("key"))))
                {
                    if (!errorLines.Contains(trimmedLine))
                    {
                        errorLines.Add(trimmedLine);
                    }
                }
            }
        }

        return errorLines.Count > 0 ? string.Join("\n", errorLines.Take(15)) : null;
    }

    /// <summary>
    /// Safely gets a string property from a JSON element
    /// </summary>
    private static string? GetJsonStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
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
                // Capture error details from the stream
                var errorContent = evt.Error ?? evt.Message ?? evt.Content ?? "Unknown error";
                result.Success = false;
                result.ErrorMessage = string.IsNullOrEmpty(result.ErrorMessage)
                    ? errorContent
                    : $"{result.ErrorMessage}\n{errorContent}";

                // Also add as a message for visibility
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
                // Capture any unhandled event types that might contain error info
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

        // Add user message
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

        // Build args for opencode run command
        var argsList = new List<string> { "run" };
        if (!string.IsNullOrEmpty(sessionId))
        {
            argsList.AddRange(new[] { "-s", sessionId });
        }
        argsList.Add("--");
        argsList.Add(EscapeArgument(prompt));

        var startInfo = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = string.Join(" ", argsList)
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
        // Default fallback models if CLI query fails
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
                // Pricing depends on the underlying model
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
                ["isAvailable"] = true // Assume available by default
            }
        };

        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            var execPath = GetExecutablePath();

            // Fetch available models dynamically using `opencode models` command
            try
            {
                using var modelsTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var modelsLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, modelsTimeoutCts.Token);

                var modelsStartInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "models"
                };

                // Configure for cross-platform with enhanced PATH
                PlatformHelper.ConfigureForCrossPlatform(modelsStartInfo);

                using var modelsProcess = new Process { StartInfo = modelsStartInfo };
                modelsProcess.Start();

                try
                {
                    var modelsOutput = await modelsProcess.StandardOutput.ReadToEndAsync(modelsLinkedCts.Token);
                    await modelsProcess.WaitForExitAsync(modelsLinkedCts.Token);

                    if (modelsProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(modelsOutput))
                    {
                        var models = ParseModelsOutput(modelsOutput);
                        if (models.Count > 0)
                        {
                            info.AvailableModels = models;

                            // Update pricing multipliers for dynamically discovered models
                            foreach (var model in models)
                            {
                                if (!info.Pricing.ModelMultipliers.ContainsKey(model))
                                {
                                    // Assign default multiplier based on model name patterns
                                    info.Pricing.ModelMultipliers[model] = GetDefaultModelMultiplier(model);
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

            // Get version with timeout to avoid hanging
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var startInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "--version"
                };

                // Configure for cross-platform with enhanced PATH
                PlatformHelper.ConfigureForCrossPlatform(startInfo);

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                try
                {
                    var version = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                    await process.WaitForExitAsync(linkedCts.Token);
                    info.Version = version.Trim();
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    info.Version = "unknown (timeout)";
                    info.AdditionalInfo["error"] = "Timed out while getting version information";
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
        }

        return info;
    }

    /// <summary>
    /// Parses the output of `opencode models` command to extract model names.
    /// Filters out log/info lines and returns only valid model identifiers.
    /// </summary>
    private static List<string> ParseModelsOutput(string output)
    {
        var models = new List<string>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Skip empty lines
            if (string.IsNullOrWhiteSpace(trimmedLine))
                continue;

            // Skip log/info lines (e.g., "INFO  2026-01-28T02:04:07 +77ms service=models.dev file={} refreshing")
            // These typically start with INFO, WARN, ERROR, DEBUG, or contain timestamp patterns
            if (IsLogLine(trimmedLine))
                continue;

            // Valid model names typically contain a slash (provider/model) or are simple identifiers
            // They should not contain spaces or special log characters
            if (IsValidModelName(trimmedLine))
            {
                models.Add(trimmedLine);
            }
        }

        return models;
    }

    /// <summary>
    /// Determines if a line appears to be a log/info line that should be filtered out.
    /// </summary>
    private static bool IsLogLine(string line)
    {
        // Check for common log level prefixes (case-insensitive)
        var logPrefixes = new[] { "INFO", "WARN", "WARNING", "ERROR", "DEBUG", "TRACE", "FATAL" };
        foreach (var prefix in logPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (line.Length == prefix.Length || char.IsWhiteSpace(line[prefix.Length]) || line[prefix.Length] == ':'))
            {
                return true;
            }
        }

        // Check for timestamp patterns at the start (e.g., "2026-01-28" or "[2026-01-28")
        if (line.Length > 10 && (char.IsDigit(line[0]) || line[0] == '[') && line.Contains('-') && line.Contains(':'))
        {
            // Likely a timestamp-prefixed log line
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\[?\d{4}-\d{2}-\d{2}"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates if a string looks like a valid model name.
    /// </summary>
    private static bool IsValidModelName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Model names should not contain spaces (log lines usually do)
        if (name.Contains(' '))
            return false;

        // Model names typically follow patterns like "provider/model" or "model:tag"
        // They should contain alphanumeric characters, slashes, colons, hyphens, underscores, or dots
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '/' && c != ':' && c != '-' && c != '_' && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets a default pricing multiplier based on the model name.
    /// </summary>
    private static decimal GetDefaultModelMultiplier(string modelName)
    {
        var lowerName = modelName.ToLowerInvariant();

        // High-tier models (opus, large, max, etc.)
        if (lowerName.Contains("opus") || lowerName.Contains("large") || lowerName.Contains("max") || lowerName.Contains("big"))
            return 5.0m;

        // Mid-tier models (sonnet, medium, pro)
        if (lowerName.Contains("sonnet") || lowerName.Contains("medium") || lowerName.Contains("pro"))
            return 1.0m;

        // Low-tier models (haiku, mini, small, nano)
        if (lowerName.Contains("haiku") || lowerName.Contains("mini") || lowerName.Contains("small") || lowerName.Contains("nano"))
            return 0.25m;

        // Local/Ollama models are typically free or very low cost
        if (lowerName.StartsWith("ollama/") || lowerName.StartsWith("local/"))
            return 0.01m;

        // Default multiplier for unknown models
        return 1.0m;
    }

    private string GetExecutablePath()
    {
        // Use PlatformHelper for cross-platform executable resolution
        var basePath = !string.IsNullOrEmpty(_executablePath) ? _executablePath : "opencode";
        return PlatformHelper.ResolveExecutablePath(basePath, _executablePath);
    }

    private static string EscapeArgument(string argument)
    {
        return PlatformHelper.EscapeArgument(argument).Trim('"', '\'');
    }

    public override Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
    {
        // OpenCode does not have built-in limits - it depends on the underlying
        // model and provider (e.g., Anthropic API, OpenAI API, etc.)
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

        // If we have a session ID, try to get summary from OpenCode CLI
        if (!string.IsNullOrEmpty(sessionId) && ConnectionMode == ProviderConnectionMode.CLI)
        {
            try
            {
                var execPath = GetExecutablePath();
                var effectiveWorkingDir = workingDirectory ?? _workingDirectory ?? Environment.CurrentDirectory;

                // OpenCode may have session commands - try to get session info
                // First try: opencode session show <id>
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
                    var error = await process.StandardError.ReadToEndAsync(linkedCts.Token);

                    await process.WaitForExitAsync(linkedCts.Token);

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        // Try to parse session information
                        var sessionSummary = ParseOpenCodeSessionOutput(output);
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
                    // Fall through to alternative approach
                }
                catch
                {
                    // Fall through to alternative approach
                }

                // Alternative: Ask the model to summarize the session
                try
                {
                    var summarizeArgs = $"run --session {sessionId} --format json \"Please provide a concise summary (1-2 sentences) of what was accomplished in this session, suitable for a git commit message.\"";

                    var summarizeStartInfo = new ProcessStartInfo
                    {
                        FileName = execPath,
                        Arguments = summarizeArgs,
                        WorkingDirectory = effectiveWorkingDir
                    };

                    PlatformHelper.ConfigureForCrossPlatform(summarizeStartInfo);

                    using var summarizeProcess = new Process { StartInfo = summarizeStartInfo };

                    using var summarizeTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var summarizeLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, summarizeTimeoutCts.Token);

                    summarizeProcess.Start();
                    summarizeProcess.StandardInput.Close();

                    var summarizeOutput = await summarizeProcess.StandardOutput.ReadToEndAsync(summarizeLinkedCts.Token);
                    await summarizeProcess.WaitForExitAsync(summarizeLinkedCts.Token);

                    if (summarizeProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(summarizeOutput))
                    {
                        var cleanedSummary = CleanOpenCodeOutput(summarizeOutput);
                        if (!string.IsNullOrWhiteSpace(cleanedSummary))
                        {
                            summary.Success = true;
                            summary.Summary = cleanedSummary;
                            summary.Source = "session";
                            return summary;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Fall through to fallback
                }
                catch
                {
                    // Fall through to fallback
                }
            }
            catch
            {
                // Fall through to fallback
            }
        }

        // Fallback: Generate summary from output if available
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

    /// <summary>
    /// Parses OpenCode session show output to extract summary information
    /// </summary>
    private static string? ParseOpenCodeSessionOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            // Look for summary or description fields
            if (root.TryGetProperty("summary", out var summaryProp))
            {
                return summaryProp.GetString();
            }

            if (root.TryGetProperty("description", out var descProp))
            {
                return descProp.GetString();
            }

            // Try to extract from messages if available
            if (root.TryGetProperty("messages", out var messagesProp))
            {
                var lastAssistantMessage = string.Empty;
                foreach (var msg in messagesProp.EnumerateArray())
                {
                    if (msg.TryGetProperty("role", out var roleProp) &&
                        roleProp.GetString() == "assistant" &&
                        msg.TryGetProperty("content", out var contentProp))
                    {
                        lastAssistantMessage = contentProp.GetString() ?? string.Empty;
                    }
                }

                if (!string.IsNullOrWhiteSpace(lastAssistantMessage) && lastAssistantMessage.Length <= 500)
                {
                    return lastAssistantMessage;
                }
            }
        }
        catch
        {
            // Not valid JSON
        }

        return null;
    }

    /// <summary>
    /// Cleans OpenCode CLI output to extract the actual response
    /// </summary>
    private static string CleanOpenCodeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        // Try to parse as JSON stream
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var textContent = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Look for message or content fields
                if (root.TryGetProperty("type", out var typeProp))
                {
                    var type = typeProp.GetString();
                    if (type == "message" || type == "assistant")
                    {
                        if (root.TryGetProperty("content", out var contentProp))
                        {
                            textContent.Append(contentProp.GetString());
                        }
                    }
                    else if (type == "done" || type == "complete")
                    {
                        if (root.TryGetProperty("output", out var outputProp))
                        {
                            var resultText = outputProp.GetString();
                            if (!string.IsNullOrWhiteSpace(resultText))
                            {
                                return resultText.Trim();
                            }
                        }
                    }
                }
            }
            catch
            {
                // Not JSON, accumulate as plain text
                if (!line.StartsWith("{"))
                {
                    textContent.AppendLine(line);
                }
            }
        }

        var result = textContent.ToString().Trim();
        return result.Length <= 500 ? result : result[..500];
    }

    /// <summary>
    /// Generates a summary from execution output when session data isn't available
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

        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            var execPath = GetExecutablePath();
            if (string.IsNullOrEmpty(execPath))
            {
                return PromptResponse.Fail("OpenCode executable path is not configured.");
            }

            try
            {
                var effectiveWorkingDir = workingDirectory ?? _workingDirectory ?? Environment.CurrentDirectory;

                // Use -p flag for simple non-interactive prompt response
                // opencode -p "your question"
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
                    return PromptResponse.Fail($"OpenCode CLI returned error: {error}");
                }

                return PromptResponse.Ok(output.Trim(), stopwatch.ElapsedMilliseconds, "opencode");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return PromptResponse.Fail($"Failed to start OpenCode CLI: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                return PromptResponse.Fail("Request was cancelled.");
            }
            catch (Exception ex)
            {
                return PromptResponse.Fail($"Error executing OpenCode CLI: {ex.Message}");
            }
        }
        else
        {
            // REST API mode
            if (_httpClient == null)
            {
                return PromptResponse.Fail("REST API client is not configured.");
            }

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

                stopwatch.Stop();

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

    // JSON models for OpenCode CLI and API output
    // Note: JsonPropertyName attributes support snake_case from CLI
    private class OpenCodeStreamEvent
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_name")]
        public string? ToolName { get; set; }

        [JsonPropertyName("tool_input")]
        public string? ToolInput { get; set; }

        [JsonPropertyName("tool_output")]
        public string? ToolOutput { get; set; }

        [JsonPropertyName("cost_usd")]
        public decimal? CostUsd { get; set; }

        [JsonPropertyName("input_tokens")]
        public int? InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int? OutputTokens { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }
    }

    private class OpenCodeApiResponse
    {
        [JsonPropertyName("output")]
        public string? Output { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("input_tokens")]
        public int? InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int? OutputTokens { get; set; }

        [JsonPropertyName("cost_usd")]
        public decimal? CostUsd { get; set; }
    }
}
