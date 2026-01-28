namespace VibeSwarm.Shared.Providers;

public abstract class ProviderBase : IProvider
{
    public Guid Id { get; protected set; }
    public string Name { get; protected set; } = string.Empty;
    public abstract ProviderType Type { get; }
    public ProviderConnectionMode ConnectionMode { get; protected set; }
    public bool IsConnected { get; protected set; }
    public string? LastConnectionError { get; protected set; }

    protected ProviderBase(Guid id, string name, ProviderConnectionMode connectionMode)
    {
        Id = id;
        Name = name;
        ConnectionMode = connectionMode;
    }

    public abstract Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    public abstract Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default);

    public abstract Task<ExecutionResult> ExecuteWithSessionAsync(
        string prompt,
        string? sessionId = null,
        string? workingDirectory = null,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute with options - provides MCP config support
    /// Default implementation delegates to ExecuteWithSessionAsync after storing MCP config path
    /// </summary>
    public virtual Task<ExecutionResult> ExecuteWithOptionsAsync(
        string prompt,
        ExecutionOptions options,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Store the MCP config path for use by child implementations
        CurrentMcpConfigPath = options.McpConfigPath;
        CurrentAdditionalArgs = options.AdditionalArgs;
        CurrentEnvironmentVariables = options.EnvironmentVariables;

        return ExecuteWithSessionAsync(
            prompt,
            options.SessionId,
            options.WorkingDirectory,
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Current MCP config path for the execution (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentMcpConfigPath { get; private set; }

    /// <summary>
    /// Current additional CLI arguments (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected List<string>? CurrentAdditionalArgs { get; private set; }

    /// <summary>
    /// Current environment variables (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected Dictionary<string, string>? CurrentEnvironmentVariables { get; private set; }

    /// <summary>
    /// Clears the execution context after a run completes
    /// </summary>
    protected void ClearExecutionContext()
    {
        CurrentMcpConfigPath = null;
        CurrentAdditionalArgs = null;
        CurrentEnvironmentVariables = null;
    }

    public abstract Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default);

    public abstract Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default);

    public abstract Task<SessionSummary> GetSessionSummaryAsync(
        string? sessionId,
        string? workingDirectory = null,
        string? fallbackOutput = null,
        CancellationToken cancellationToken = default);

    public abstract Task<PromptResponse> GetPromptResponseAsync(
        string prompt,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}
