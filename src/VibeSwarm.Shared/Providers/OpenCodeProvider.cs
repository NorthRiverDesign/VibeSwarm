using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

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
                BaseAddress = new Uri(_apiEndpoint)
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
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = "version",
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

        var response = await _httpClient.GetAsync("/health", cancellationToken);
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

        // Build arguments - opencode uses 'run' command with --format json
        var args = new List<string> { "run", "--format", "json" };

        if (!string.IsNullOrEmpty(sessionId))
        {
            args.AddRange(new[] { "--session", sessionId });
        }

        // Add the prompt at the end
        args.Add($"\"{EscapeArgument(prompt)}\"");

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
        var currentAssistantMessage = new System.Text.StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            outputBuilder.Add(e.Data);

            try
            {
                var jsonEvent = JsonSerializer.Deserialize<OpenCodeStreamEvent>(e.Data, JsonOptions);
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

        process.Start();
        process.BeginOutputReadLine();

        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

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

        if (!result.Success && !string.IsNullOrEmpty(error))
        {
            result.ErrorMessage = error;
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

        var args = $"run \"{EscapeArgument(prompt)}\"";
        if (!string.IsNullOrEmpty(sessionId))
        {
            args = $"run --session {sessionId} \"{EscapeArgument(prompt)}\"";
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
        var info = new ProviderInfo
        {
            AvailableModels = new List<string>
            {
                "anthropic/claude-sonnet-4-20250514",
                "anthropic/claude-opus-4-20250514",
                "openai/gpt-4o",
                "openai/o1"
            },
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
                    Arguments = "version",
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
        return "opencode";
    }

    private static string EscapeArgument(string argument)
    {
        return argument.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // JSON models for OpenCode CLI and API output
    private class OpenCodeStreamEvent
    {
        public string? Type { get; set; }
        public string? SessionId { get; set; }
        public string? Content { get; set; }
        public string? ToolName { get; set; }
        public string? ToolInput { get; set; }
        public string? ToolOutput { get; set; }
        public decimal? CostUsd { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
    }

    private class OpenCodeApiResponse
    {
        public string? Output { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? SessionId { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public decimal? CostUsd { get; set; }
    }
}
