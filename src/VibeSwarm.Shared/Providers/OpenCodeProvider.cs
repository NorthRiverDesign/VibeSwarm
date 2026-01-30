using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using VibeSwarm.Shared.Providers.OpenCode;
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

        // Build arguments for opencode non-interactive mode
        // Using --prompt for non-interactive mode which runs a single prompt and exits
        // Working directory is set via ProcessStartInfo.WorkingDirectory in CreateCliProcess
        var args = new List<string>();

        // Continue a specific session if provided
        if (!string.IsNullOrEmpty(sessionId))
        {
            args.AddRange(new[] { "--session", sessionId });
        }

        if (CurrentEnvironmentVariables != null &&
            CurrentEnvironmentVariables.TryGetValue("OPENCODE_MODEL", out var model) &&
            !string.IsNullOrEmpty(model))
        {
            args.AddRange(new[] { "--model", model });
        }

        if (CurrentAdditionalArgs != null)
        {
            args.AddRange(CurrentAdditionalArgs);
        }

        // Add the prompt using --prompt flag for non-interactive mode
        args.Add("--prompt");
        args.Add($"\"{EscapeCliArgument(prompt)}\"");

        var fullArguments = string.Join(" ", args);
        var fullCommand = $"{execPath} {fullArguments}";

        using var process = CreateCliProcess(execPath, fullArguments, effectiveWorkingDir);

        var outputBuilder = new List<string>();
        var errorBuilder = new System.Text.StringBuilder();
        var currentAssistantMessage = new System.Text.StringBuilder();

        var outputComplete = new TaskCompletionSource<bool>();
        var errorComplete = new TaskCompletionSource<bool>();

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

            // Try to parse as JSON first
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

            // Plain text output
            currentAssistantMessage.AppendLine(e.Data);
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
                errorComplete.TrySetResult(true);
                return;
            }

            if (!string.IsNullOrEmpty(e.Data))
            {
                errorBuilder.AppendLine(e.Data);
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

        result.ProcessId = process.Id;
        result.CommandUsed = fullCommand;
        ReportProcessStarted(process.Id, progress, fullCommand);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Start initialization monitor
        using var initMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var initializationMonitorTask = CreateInitializationMonitorAsync(
            () => outputBuilder.Count > 0,
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
        result.Output = string.Join("\n", outputBuilder);

        var stderrError = errorBuilder.ToString();

        if (!result.Success)
        {
            var errorMessages = new List<string>();

            if (!string.IsNullOrWhiteSpace(stderrError))
            {
                errorMessages.Add($"[stderr] {stderrError.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                errorMessages.Add(result.ErrorMessage);
            }

            var errorFromOutput = OpenCodeOutputParser.ExtractErrorFromOutput(outputBuilder);
            if (!string.IsNullOrWhiteSpace(errorFromOutput))
            {
                errorMessages.Add(errorFromOutput);
            }

            if (errorMessages.Count == 0 && outputBuilder.Count > 0)
            {
                var lastLines = outputBuilder.TakeLast(10).ToList();
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

        // Build arguments for non-interactive mode
        var argsList = new List<string>();
        if (!string.IsNullOrEmpty(sessionId))
        {
            argsList.AddRange(new[] { "--session", sessionId });
        }
        argsList.Add("--prompt");
        argsList.Add($"\"{EscapeCliArgument(prompt)}\"");

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

            var args = $"--prompt \"{EscapeCliArgument(prompt)}\"";
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
}
