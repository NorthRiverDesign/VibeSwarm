using VibeSwarm.Shared.Services;

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
        // Store execution options for use by child implementations
        CurrentMcpConfigPath = options.McpConfigPath;
        CurrentAdditionalArgs = options.AdditionalArgs;
        CurrentEnvironmentVariables = options.EnvironmentVariables;
        CurrentModel = options.Model;
        CurrentTitle = options.Title;
        CurrentAgent = options.Agent;
        CurrentAttachedFiles = options.AttachedFiles;
        CurrentOutputFormat = options.OutputFormat;
        CurrentContinueLastSession = options.ContinueLastSession;
        CurrentSystemPrompt = options.SystemPrompt;
        CurrentAppendSystemPrompt = options.AppendSystemPrompt;
        CurrentMaxTurns = options.MaxTurns;
        CurrentMaxBudgetUsd = options.MaxBudgetUsd;
        CurrentAdditionalDirectories = options.AdditionalDirectories;
        CurrentTimeoutSeconds = options.TimeoutSeconds;
        CurrentAllowedTools = options.AllowedTools;
        CurrentExcludedTools = options.ExcludedTools;
        CurrentDisallowedTools = options.DisallowedTools;
        CurrentUseWorktree = options.UseWorktree;
        CurrentUseAutopilot = options.UseAutopilot;
        CurrentFromPullRequest = options.FromPullRequest;
        CurrentInitMode = options.InitMode;
        CurrentForkSession = options.ForkSession;
        CurrentUseAltScreen = options.UseAltScreen;

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
    /// Current model to use (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentModel { get; private set; }

    /// <summary>
    /// Current session title (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentTitle { get; private set; }

    /// <summary>
    /// Current agent to use (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentAgent { get; private set; }

    /// <summary>
    /// Current attached files (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected List<string>? CurrentAttachedFiles { get; private set; }

    /// <summary>
    /// Current output format (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentOutputFormat { get; private set; }

    /// <summary>
    /// Whether to continue the last session (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected bool CurrentContinueLastSession { get; private set; }

    /// <summary>
    /// System prompt override (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentSystemPrompt { get; private set; }

    /// <summary>
    /// System prompt to append (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentAppendSystemPrompt { get; private set; }

    /// <summary>
    /// Maximum number of agentic turns (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected int? CurrentMaxTurns { get; private set; }

    /// <summary>
    /// Maximum budget in USD (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected decimal? CurrentMaxBudgetUsd { get; private set; }

    /// <summary>
    /// Additional working directories (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected List<string>? CurrentAdditionalDirectories { get; private set; }

    /// <summary>
    /// Timeout in seconds (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected int? CurrentTimeoutSeconds { get; private set; }

    /// <summary>
    /// Allowed tools filter (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected List<string>? CurrentAllowedTools { get; private set; }

    /// <summary>
    /// Excluded tools filter (set by ExecuteWithOptionsAsync)
    /// </summary>
    protected List<string>? CurrentExcludedTools { get; private set; }

    /// <summary>
    /// Disallowed tools (Claude --disallowed-tools, set by ExecuteWithOptionsAsync)
    /// </summary>
    protected List<string>? CurrentDisallowedTools { get; private set; }

    /// <summary>
    /// Whether to use isolated git worktree (Claude --worktree, set by ExecuteWithOptionsAsync)
    /// </summary>
    protected bool CurrentUseWorktree { get; private set; }

    /// <summary>
    /// Whether to use autopilot mode (Copilot autopilot, set by ExecuteWithOptionsAsync)
    /// </summary>
    protected bool CurrentUseAutopilot { get; private set; }

    /// <summary>
    /// PR number/URL for session linking (Claude --from-pr, set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentFromPullRequest { get; private set; }

    /// <summary>
    /// Init mode for setup hooks (Claude --init/--init-only/--maintenance, set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentInitMode { get; private set; }

    /// <summary>
    /// Whether to fork the session (OpenCode --fork, set by ExecuteWithOptionsAsync)
    /// </summary>
    protected bool CurrentForkSession { get; private set; }

    /// <summary>
    /// Whether to use alt-screen mode (Copilot --alt-screen, set by ExecuteWithOptionsAsync)
    /// </summary>
    protected bool CurrentUseAltScreen { get; private set; }

    /// <summary>
    /// Clears the execution context after a run completes
    /// </summary>
    protected void ClearExecutionContext()
    {
        CurrentMcpConfigPath = null;
        CurrentAdditionalArgs = null;
        CurrentEnvironmentVariables = null;
        CurrentModel = null;
        CurrentTitle = null;
        CurrentAgent = null;
        CurrentAttachedFiles = null;
        CurrentOutputFormat = null;
        CurrentContinueLastSession = false;
        CurrentSystemPrompt = null;
        CurrentAppendSystemPrompt = null;
        CurrentMaxTurns = null;
        CurrentMaxBudgetUsd = null;
        CurrentAdditionalDirectories = null;
        CurrentTimeoutSeconds = null;
        CurrentAllowedTools = null;
        CurrentExcludedTools = null;
        CurrentDisallowedTools = null;
        CurrentUseWorktree = false;
        CurrentUseAutopilot = false;
        CurrentFromPullRequest = null;
        CurrentInitMode = null;
        CurrentForkSession = false;
        CurrentUseAltScreen = false;
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

    public abstract Task<CliUpdateResult> UpdateCliAsync(CancellationToken cancellationToken = default);
}
