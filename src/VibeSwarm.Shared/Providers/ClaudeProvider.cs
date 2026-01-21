using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory ?? Environment.CurrentDirectory
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await process.WaitForExitAsync(cancellationToken);

        IsConnected = process.ExitCode == 0;
        return IsConnected;
    }

    private async Task<bool> TestRestConnectionAsync(CancellationToken cancellationToken)
    {
        if (_httpClient == null)
        {
            IsConnected = false;
            return false;
        }

        var response = await _httpClient.GetAsync("/v1/models", cancellationToken);
        IsConnected = response.IsSuccessStatusCode;
        return IsConnected;
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

        // Build arguments for JSON output with session support
        // -p: non-interactive print mode
        // --output-format stream-json: JSON streaming output
        // --verbose: required for stream-json format
        var args = new List<string> { "-p", $"\"{EscapeArgument(prompt)}\"", "--output-format", "stream-json", "--verbose" };

        if (!string.IsNullOrEmpty(sessionId))
        {
            args.AddRange(new[] { "--resume", sessionId });
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = effectiveWorkingDir
        };

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
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait for the process to exit - this can take several minutes for complex agent tasks
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // If cancelled, try to kill the process
            try { process.Kill(entireProcessTree: true); } catch { }
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
            }
        };

        if (ConnectionMode == ProviderConnectionMode.CLI)
        {
            try
            {
                var execPath = GetExecutablePath();
                var startInfo = new ProcessStartInfo
                {
                    FileName = execPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                var version = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                info.Version = version.Trim();
            }
            catch
            {
                info.Version = "unknown";
            }
        }

        return info;
    }

    private string GetExecutablePath()
    {
        if (!string.IsNullOrEmpty(_executablePath))
        {
            return _executablePath;
        }
        return "claude";
    }

    private static string EscapeArgument(string argument)
    {
        return argument.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
