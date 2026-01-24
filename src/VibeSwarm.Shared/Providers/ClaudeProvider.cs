using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

public class ClaudeProvider : ProviderBase
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

    public override ProviderType Type => ProviderType.Claude;

    public ClaudeProvider(Provider config)
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
                _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
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
            LastConnectionError = "Claude executable path is not configured.";
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
                RedirectStandardInput = true,  // Redirect stdin to prevent hanging on prompts
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workingDirectory ?? Environment.CurrentDirectory
            };

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
                LastConnectionError = $"CLI test timed out after 10 seconds. Command: {execPath} --version\n" +
                    $"Working directory: {_workingDirectory ?? Environment.CurrentDirectory}\n" +
                    "This usually indicates:\n" +
                    "  - The CLI is waiting for authentication (check if 'claude' works in terminal)\n" +
                    "  - The CLI is trying to access the network and timing out\n" +
                    "  - The process doesn't have access to required environment variables\n" +
                    "  - The service account doesn't have permission to run the CLI";
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
            // Executable not found or can't be started
            IsConnected = false;
            LastConnectionError = $"Failed to start Claude CLI: {ex.Message}. " +
                $"Executable path: '{execPath}'. " +
                $"Ensure the Claude CLI is installed and the path is correct, or leave ExecutablePath empty to use 'claude' from PATH.";
            return false;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastConnectionError = $"Unexpected error testing Claude CLI connection: {ex.GetType().Name}: {ex.Message}";
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
            var response = await _httpClient.GetAsync("/v1/models", cancellationToken);
            IsConnected = response.IsSuccessStatusCode;

            if (!IsConnected)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                LastConnectionError = $"REST API test failed. Status: {(int)response.StatusCode} {response.ReasonPhrase}. " +
                    $"Endpoint: {_apiEndpoint}/v1/models. Response: {responseBody}";
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
            LastConnectionError = $"Failed to connect to Claude API at {_apiEndpoint}: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastConnectionError = $"Unexpected error testing Claude REST API: {ex.GetType().Name}: {ex.Message}";
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
            return await ExecuteRestWithSessionAsync(prompt, progress, cancellationToken);
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
                ErrorMessage = "Claude executable path is not configured."
            };
        }

        var result = new ExecutionResult { Messages = new List<ExecutionMessage>() };
        var effectiveWorkingDir = workingDirectory ?? _workingDirectory ?? Environment.CurrentDirectory;

        // Build arguments for non-interactive execution with JSON streaming output
        // -p: non-interactive print mode (runs to completion without user input)
        // --output-format stream-json: JSON streaming output for parsing progress
        // --verbose: required for stream-json format
        // --dangerously-skip-permissions: skip permission prompts (auto-accept tool use)
        var args = new List<string>();

        // Add the prompt with -p flag for non-interactive mode
        args.Add("-p");
        args.Add($"\"{EscapeArgument(prompt)}\"");

        // Add output format flags
        args.Add("--output-format");
        args.Add("stream-json");
        args.Add("--verbose");

        // Skip permission prompts for automated execution
        args.Add("--dangerously-skip-permissions");

        if (!string.IsNullOrEmpty(sessionId))
        {
            args.Add("--resume");
            args.Add(sessionId);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = string.Join(" ", args),
            WorkingDirectory = effectiveWorkingDir,
        };

        // Use cross-platform configuration
        PlatformHelper.ConfigureForCrossPlatform(startInfo);

        using var process = new Process { StartInfo = startInfo };

        var outputBuilder = new List<string>();
        var errorBuilder = new System.Text.StringBuilder();
        var currentAssistantMessage = new System.Text.StringBuilder();

        // Use TaskCompletionSource to ensure all output is processed before continuing
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

        // Capture process ID immediately after starting
        result.ProcessId = process.Id;

        // Report process ID through progress callback for immediate tracking
        progress?.Report(new ExecutionProgress
        {
            CurrentMessage = "CLI process started successfully",
            ProcessId = process.Id,
            IsStreaming = false
        });

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

            // Stream raw output line to UI
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
                // Non-JSON output, append to current message
                currentAssistantMessage.Append(e.Data);
            }
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

                // Stream error output line to UI
                progress?.Report(new ExecutionProgress
                {
                    OutputLine = e.Data,
                    IsErrorOutput = true
                });
            }
        };

        // Close stdin immediately to signal no user input is coming
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
        ClaudeStreamEvent evt,
        ExecutionResult result,
        System.Text.StringBuilder currentMessage,
        IProgress<ExecutionProgress>? progress)
    {
        switch (evt.Type)
        {
            case "system":
                // System init event contains session_id
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
                // Assistant event contains a message object with content array
                if (evt.Message?.Content != null)
                {
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
                            // Finalize any pending text message
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
                // User message (tool results come back as user messages)
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
                // Final result with metrics
                if (evt.TotalCostUsd.HasValue)
                {
                    result.CostUsd = evt.TotalCostUsd;
                }
                if (evt.Usage != null)
                {
                    result.InputTokens = evt.Usage.InputTokens;
                    result.OutputTokens = evt.Usage.OutputTokens;
                }
                if (!string.IsNullOrEmpty(evt.SessionId))
                {
                    result.SessionId = evt.SessionId;
                }
                if (!string.IsNullOrEmpty(evt.Result))
                {
                    // Store final result as output
                    result.Output = evt.Result;
                }
                break;
        }
    }

    private async Task<ExecutionResult> ExecuteRestWithSessionAsync(
        string prompt,
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
            model = "claude-sonnet-4-20250514",
            max_tokens = 4096,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/v1/messages", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var apiResult = await response.Content.ReadFromJsonAsync<ClaudeApiResponse>(JsonOptions, cancellationToken);

            if (apiResult != null)
            {
                result.SessionId = apiResult.Id;
                result.InputTokens = apiResult.Usage?.InputTokens;
                result.OutputTokens = apiResult.Usage?.OutputTokens;

                var content = apiResult.Content?
                    .Where(c => c.Type == "text")
                    .Select(c => c.Text)
                    .FirstOrDefault() ?? "";

                result.Messages.Add(new ExecutionMessage
                {
                    Role = "assistant",
                    Content = content,
                    Timestamp = DateTime.UtcNow
                });

                result.Output = content;
                result.Success = true;
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
            throw new InvalidOperationException("Claude executable path is not configured.");
        }

        var args = $"-p \"{EscapeArgument(prompt)}\"";
        if (!string.IsNullOrEmpty(sessionId))
        {
            args += $" --resume {sessionId}";
        }

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
            throw new InvalidOperationException($"Claude execution failed: {error}");
        }

        return output;
    }

    private async Task<string> ExecuteRestAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_httpClient == null)
        {
            throw new InvalidOperationException("REST client is not configured.");
        }

        var request = new
        {
            model = "claude-sonnet-4-20250514",
            max_tokens = 4096,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/messages", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClaudeApiResponse>(JsonOptions, cancellationToken);

        if (result?.Content != null && result.Content.Length > 0)
        {
            return string.Join("", result.Content
                .Where(c => c.Type == "text")
                .Select(c => c.Text));
        }

        return string.Empty;
    }

    public override async Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = new ProviderInfo
        {
            AvailableModels = new List<string>
            {
                "claude-sonnet-4-20250514",
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
                    ["claude-sonnet-4-20250514"] = 1.0m,
                    ["claude-opus-4-20250514"] = 5.0m,
                    ["claude-3-5-haiku-20241022"] = 0.27m
                }
            },
            AdditionalInfo = new Dictionary<string, object>
            {
                ["isAvailable"] = true // Assume available by default
            }
        };

        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            try
            {
                var execPath = GetExecutablePath();

                // Get version
                var versionInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var versionProcess = new Process { StartInfo = versionInfo };
                versionProcess.Start();
                var version = await versionProcess.StandardOutput.ReadToEndAsync(cancellationToken);
                await versionProcess.WaitForExitAsync(cancellationToken);
                info.Version = version.Trim();

                // Check usage to determine availability
                var usageInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "/usage",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var usageProcess = new Process { StartInfo = usageInfo };
                usageProcess.Start();
                var usageOutput = await usageProcess.StandardOutput.ReadToEndAsync(cancellationToken);
                var usageError = await usageProcess.StandardError.ReadToEndAsync(cancellationToken);
                await usageProcess.WaitForExitAsync(cancellationToken);

                if (usageProcess.ExitCode == 0 && !string.IsNullOrEmpty(usageOutput))
                {
                    info.AdditionalInfo["usageOutput"] = usageOutput;

                    // Parse usage output to check for availability issues
                    var usageLower = usageOutput.ToLowerInvariant();

                    // Check for rate limiting or usage exceeded messages
                    if (usageLower.Contains("limit exceeded") ||
                        usageLower.Contains("rate limit") ||
                        usageLower.Contains("quota exceeded") ||
                        usageLower.Contains("no remaining") ||
                        usageLower.Contains("usage limit"))
                    {
                        info.AdditionalInfo["isAvailable"] = false;
                        info.AdditionalInfo["unavailableReason"] = "Provider usage limit reached. Please wait or upgrade your plan.";
                    }

                    // Check for authentication issues
                    if (usageLower.Contains("not authenticated") ||
                        usageLower.Contains("authentication failed") ||
                        usageLower.Contains("invalid api key") ||
                        usageLower.Contains("unauthorized"))
                    {
                        info.AdditionalInfo["isAvailable"] = false;
                        info.AdditionalInfo["unavailableReason"] = "Provider authentication failed. Please check your API key.";
                    }

                    // Try to extract remaining tokens/usage if available
                    // This depends on the actual output format of `claude /usage`
                    if (usageLower.Contains("remaining"))
                    {
                        info.AdditionalInfo["hasRemainingUsage"] = true;
                    }
                }
                else if (!string.IsNullOrEmpty(usageError))
                {
                    // Check if /usage is not a valid command (older versions)
                    if (!usageError.Contains("unknown") && !usageError.Contains("invalid"))
                    {
                        info.AdditionalInfo["usageCheckError"] = usageError;
                    }
                    // If /usage command doesn't exist, assume available (older CLI versions)
                }
            }
            catch (Exception ex)
            {
                info.Version = "unknown";
                info.AdditionalInfo["error"] = ex.Message;
                // Don't mark as unavailable just because we can't get version/usage
            }
        }
        else if (ConnectionMode == ProviderConnectionMode.REST)
        {
            // For REST mode, we already checked connection in TestConnectionAsync
            info.Version = "REST API";
        }

        return info;
    }

    private string GetExecutablePath()
    {
        // Use PlatformHelper for cross-platform executable resolution
        var basePath = !string.IsNullOrEmpty(_executablePath) ? _executablePath : "claude";
        return PlatformHelper.ResolveExecutablePath(basePath, _executablePath);
    }

    private static string EscapeArgument(string argument)
    {
        return PlatformHelper.EscapeArgument(argument).Trim('"', '\'');
    }

    public override async Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default)
    {
        var limits = new UsageLimits
        {
            LimitType = UsageLimitType.SessionLimit,
            IsLimitReached = false
        };

        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            try
            {
                var execPath = GetExecutablePath();

                // Claude CLI has a /usage command to check session limits
                var startInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "/usage",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
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
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    limits.Message = "Unable to check session limits (timeout)";
                    return limits;
                }

                var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    var outputLower = output.ToLowerInvariant();

                    // Check for session limit indicators
                    if (outputLower.Contains("limit exceeded") ||
                        outputLower.Contains("session limit") ||
                        outputLower.Contains("no remaining") ||
                        outputLower.Contains("quota exceeded"))
                    {
                        limits.IsLimitReached = true;
                        limits.Message = "Session limit reached. Please wait for the limit to reset.";
                    }
                    else if (outputLower.Contains("remaining"))
                    {
                        // Try to parse remaining sessions
                        var match = System.Text.RegularExpressions.Regex.Match(
                            output, @"(\d+)\s*(?:of\s*)?(\d+)?\s*(?:remaining|sessions?|left)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (match.Success)
                        {
                            if (int.TryParse(match.Groups[1].Value, out var remaining))
                            {
                                if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var total))
                                {
                                    limits.CurrentUsage = total - remaining;
                                    limits.MaxUsage = total;
                                }
                                else
                                {
                                    // Only remaining is known
                                    limits.CurrentUsage = 0; // Unknown actual usage
                                    limits.MaxUsage = remaining; // Treat remaining as available
                                }
                                limits.IsLimitReached = remaining <= 0;
                            }
                        }

                        limits.Message = limits.IsLimitReached
                            ? "Session limit reached"
                            : $"Sessions available: {limits.MaxUsage - (limits.CurrentUsage ?? 0)} remaining";
                    }

                    // Check for reset time
                    var resetMatch = System.Text.RegularExpressions.Regex.Match(
                        output, @"reset[s]?\s*(?:at|in|on)?\s*:?\s*(.+?)(?:\n|$)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (resetMatch.Success)
                    {
                        if (DateTime.TryParse(resetMatch.Groups[1].Value.Trim(), out var resetTime))
                        {
                            limits.ResetTime = resetTime;
                        }
                    }

                    if (string.IsNullOrEmpty(limits.Message))
                    {
                        limits.Message = "Session limits available";
                    }
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    // /usage command might not exist in older versions
                    if (error.Contains("unknown") || error.Contains("invalid"))
                    {
                        limits.Message = "Usage command not supported in this Claude CLI version";
                    }
                    else
                    {
                        limits.Message = $"Unable to check session limits: {error.Trim()}";
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                limits.Message = "Claude CLI not found";
            }
            catch (Exception ex)
            {
                limits.Message = $"Unable to check session limits: {ex.Message}";
            }
        }
        else
        {
            // REST API mode - check via API if possible
            limits.LimitType = UsageLimitType.RateLimit;
            limits.Message = "REST API rate limits apply (check API response headers)";
        }

        return limits;
    }

    // JSON models for Claude CLI stream-json output
    private class ClaudeStreamEvent
    {
        public string? Type { get; set; }
        public string? Subtype { get; set; }
        public string? SessionId { get; set; }
        public ClaudeMessage? Message { get; set; }
        // Result event fields
        public string? Result { get; set; }
        public decimal? TotalCostUsd { get; set; }
        public UsageInfo? Usage { get; set; }
    }

    private class ClaudeMessage
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Role { get; set; }
        public string? Model { get; set; }
        public ContentBlock[]? Content { get; set; }
        public UsageInfo? Usage { get; set; }
    }

    private class ContentBlock
    {
        public string? Type { get; set; }
        // For text content
        public string? Text { get; set; }
        // For tool_use content
        public string? Id { get; set; }
        public string? Name { get; set; }
        public JsonElement? Input { get; set; }
        // For tool_result content
        public string? ToolUseId { get; set; }
        public string? Content { get; set; }
    }

    private class UsageInfo
    {
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public int? CacheReadInputTokens { get; set; }
        public int? CacheCreationInputTokens { get; set; }
    }

    // JSON models for Claude REST API
    private class ClaudeApiResponse
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Role { get; set; }
        public ContentBlock[]? Content { get; set; }
        public string? Model { get; set; }
        public string? StopReason { get; set; }
        public UsageInfo? Usage { get; set; }
    }
}
