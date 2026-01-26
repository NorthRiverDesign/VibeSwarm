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
    /// Execute a prompt with full session management, message streaming, and additional options
    /// </summary>
    Task<ExecutionResult> ExecuteWithOptionsAsync(
        string prompt,
        ExecutionOptions options,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get information about the provider's capabilities
    /// </summary>
    Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get current usage limits for the provider
    /// </summary>
    Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a summary of what was accomplished during a session.
    /// Used to pre-populate commit messages after job completion.
    /// </summary>
    /// <param name="sessionId">The session ID from a previous execution</param>
    /// <param name="workingDirectory">The working directory where the session ran</param>
    /// <param name="fallbackOutput">Fallback output to summarize if session data isn't available</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A concise summary suitable for a commit message</returns>
    Task<SessionSummary> GetSessionSummaryAsync(
        string? sessionId,
        string? workingDirectory = null,
        string? fallbackOutput = null,
        CancellationToken cancellationToken = default);
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
    /// The AI model that was used for this execution (e.g., "claude-sonnet-4-20250514")
    /// </summary>
    public string? ModelUsed { get; set; }

    /// <summary>
    /// Process ID of the CLI process (for tracking and cancellation)
    /// </summary>
    public int? ProcessId { get; set; }

    /// <summary>
    /// True if the execution was paused due to an interaction request
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Details of the pending interaction if IsPaused is true
    /// </summary>
    public InteractionInfo? PendingInteraction { get; set; }
}

/// <summary>
/// Information about a pending user interaction
/// </summary>
public class InteractionInfo
{
    /// <summary>
    /// The prompt/question being asked
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// The type of interaction (confirmation, input, choice, permission, etc.)
    /// </summary>
    public string Type { get; set; } = "unknown";

    /// <summary>
    /// Available choices if applicable
    /// </summary>
    public List<string>? Choices { get; set; }

    /// <summary>
    /// Suggested default response
    /// </summary>
    public string? DefaultResponse { get; set; }

    /// <summary>
    /// Timestamp when the interaction was detected
    /// </summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
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

    /// <summary>
    /// Raw output line from the CLI process (for streaming to UI)
    /// </summary>
    public string? OutputLine { get; set; }

    /// <summary>
    /// True if OutputLine is from stderr
    /// </summary>
    public bool IsErrorOutput { get; set; }

    /// <summary>
    /// True if an interaction is being requested by the CLI agent
    /// </summary>
    public bool IsInteractionRequested { get; set; }

    /// <summary>
    /// Details of the interaction request if IsInteractionRequested is true
    /// </summary>
    public InteractionInfo? InteractionRequest { get; set; }
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

/// <summary>
/// Information about provider usage limits
/// </summary>
public class UsageLimits
{
    /// <summary>
    /// The type of limit this provider has
    /// </summary>
    public UsageLimitType LimitType { get; set; } = UsageLimitType.None;

    /// <summary>
    /// Whether the limit has been reached
    /// </summary>
    public bool IsLimitReached { get; set; }

    /// <summary>
    /// Current usage count (requests, sessions, etc.)
    /// </summary>
    public int? CurrentUsage { get; set; }

    /// <summary>
    /// Maximum allowed usage (if known)
    /// </summary>
    public int? MaxUsage { get; set; }

    /// <summary>
    /// When the limit resets (if known)
    /// </summary>
    public DateTime? ResetTime { get; set; }

    /// <summary>
    /// Human-readable message about the limit status
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Percentage of limit used (0-100, null if unknown)
    /// </summary>
    public int? PercentUsed => (CurrentUsage.HasValue && MaxUsage.HasValue && MaxUsage > 0)
        ? (int)((CurrentUsage.Value / (double)MaxUsage.Value) * 100)
        : null;
}

/// <summary>
/// Types of usage limits that providers can have
/// </summary>
public enum UsageLimitType
{
    /// <summary>
    /// No limits (e.g., OpenCode depends on underlying model/provider)
    /// </summary>
    None,

    /// <summary>
    /// Premium request limit (e.g., GitHub Copilot)
    /// </summary>
    PremiumRequests,

    /// <summary>
    /// Session-based limit (e.g., Claude CLI)
    /// </summary>
    SessionLimit,

    /// <summary>
    /// Token-based limit
    /// </summary>
    TokenLimit,

    /// <summary>
    /// Rate limit (requests per time period)
    /// </summary>
    RateLimit
}

/// <summary>
/// Summary of work accomplished during a provider session
/// </summary>
public class SessionSummary
{
    /// <summary>
    /// Whether the summary was successfully retrieved
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// A concise summary suitable for a commit message (typically 1-3 lines)
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// A more detailed description of changes made
    /// </summary>
    public string? DetailedDescription { get; set; }

    /// <summary>
    /// List of files that were modified (if available)
    /// </summary>
    public List<string> ModifiedFiles { get; set; } = new();

    /// <summary>
    /// Error message if summary retrieval failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The source of the summary (e.g., "session", "output", "fallback")
    /// </summary>
    public string? Source { get; set; }
}

/// <summary>
/// Options for executing prompts with providers
/// </summary>
public class ExecutionOptions
{
    /// <summary>
    /// Session ID for continuing a previous session
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Working directory for the execution
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Path to MCP configuration file to inject into the CLI command
    /// </summary>
    public string? McpConfigPath { get; set; }

    /// <summary>
    /// Additional CLI arguments to pass to the provider
    /// </summary>
    public List<string>? AdditionalArgs { get; set; }

    /// <summary>
    /// Environment variables to set for the execution
    /// </summary>
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Creates ExecutionOptions from the legacy parameters
    /// </summary>
    public static ExecutionOptions FromLegacy(string? sessionId = null, string? workingDirectory = null)
    {
        return new ExecutionOptions
        {
            SessionId = sessionId,
            WorkingDirectory = workingDirectory
        };
    }
}
