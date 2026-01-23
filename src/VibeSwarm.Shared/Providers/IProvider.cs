namespace VibeSwarm.Shared.Providers;

public interface IProvider
{
    Guid Id { get; }
    string Name { get; }
    ProviderType Type { get; }
    ProviderConnectionMode ConnectionMode { get; }
    bool IsConnected { get; }
    string? LastConnectionError { get; }

    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a prompt with full session management and message streaming
    /// </summary>
    Task<ExecutionResult> ExecuteWithSessionAsync(
        string prompt,
        string? sessionId = null,
        string? workingDirectory = null,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get information about the provider's capabilities
    /// </summary>
    Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an execution including session info and messages
/// </summary>
public class ExecutionResult
{
    public bool Success { get; set; }
    public string? SessionId { get; set; }
    public string? Output { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ExecutionMessage> Messages { get; set; } = new();
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }

    /// <summary>
    /// Process ID of the CLI process (for tracking and cancellation)
    /// </summary>
    public int? ProcessId { get; set; }
}

/// <summary>
/// A message from the execution
/// </summary>
public class ExecutionMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? ToolName { get; set; }
    public string? ToolInput { get; set; }
    public string? ToolOutput { get; set; }
}

/// <summary>
/// Progress update during execution
/// </summary>
public class ExecutionProgress
{
    public string? CurrentMessage { get; set; }
    public string? ToolName { get; set; }
    public bool IsStreaming { get; set; }
    public int? TokensUsed { get; set; }

    /// <summary>
    /// Process ID (reported once when process starts)
    /// </summary>
    public int? ProcessId { get; set; }
}

/// <summary>
/// Information about a provider's capabilities
/// </summary>
public class ProviderInfo
{
    public string Version { get; set; } = string.Empty;
    public List<string> AvailableModels { get; set; } = new();
    public List<AgentInfo> AvailableAgents { get; set; } = new();
    public PricingInfo? Pricing { get; set; }
    public Dictionary<string, object> AdditionalInfo { get; set; } = new();
}

/// <summary>
/// Information about an available agent
/// </summary>
public class AgentInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>
/// Pricing information for the provider
/// </summary>
public class PricingInfo
{
    public decimal? InputTokenPricePerMillion { get; set; }
    public decimal? OutputTokenPricePerMillion { get; set; }
    public string? Currency { get; set; } = "USD";
    public Dictionary<string, decimal>? ModelMultipliers { get; set; }
}
