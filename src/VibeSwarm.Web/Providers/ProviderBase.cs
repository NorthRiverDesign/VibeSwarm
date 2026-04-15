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
    public virtual async Task<ExecutionResult> ExecuteWithOptionsAsync(
        string prompt,
        ExecutionOptions options,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Store execution options for use by child implementations
        ApplyOptions(options);

        try
        {
            return await ExecuteWithSessionAsync(
                prompt,
                options.SessionId,
                options.WorkingDirectory,
                progress,
                cancellationToken);
        }
        finally
        {
            ClearExecutionContext();
        }
    }

    /// <summary>
    /// Environment variables that should always be applied for this provider instance.
    /// </summary>
    protected Dictionary<string, string>? BaseEnvironmentVariables { get; set; }

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
    protected Dictionary<string, string>? CurrentEnvironmentVariables { get; set; }

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
	/// Whether to use Claude bare mode (set by ExecuteWithOptionsAsync)
	/// </summary>
	protected bool CurrentUseBareMode { get; private set; }

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
    /// Reasoning effort level (provider-specific, e.g. low/medium/high/xhigh)
    /// </summary>
    protected string? CurrentReasoningEffort { get; private set; }

    /// <summary>
    /// Whether to disable large context window (Claude CLAUDE_CODE_DISABLE_1M_CONTEXT)
    /// </summary>
    protected bool CurrentDisableLargeContext { get; private set; }

    /// <summary>
    /// Path to bash environment file (Copilot --bash-env)
    /// </summary>
    protected string? CurrentBashEnvPath { get; private set; }

    /// <summary>
    /// Permission mode for automated execution (Claude --permission-mode, set by ExecuteWithOptionsAsync)
    /// </summary>
    protected string? CurrentPermissionMode { get; private set; }

    /// <summary>Pre-assigned session UUID (Claude --session-id).</summary>
    protected string? CurrentPreassignedSessionId { get; private set; }

    /// <summary>CLI-level automatic fallback model (Claude --fallback-model).</summary>
    protected string? CurrentFallbackModel { get; private set; }

    /// <summary>Use only the supplied MCP config file (Claude --strict-mcp-config).</summary>
    protected bool CurrentStrictMcpConfig { get; private set; }

    /// <summary>Settings sources (Claude --setting-sources).</summary>
    protected string? CurrentSettingSources { get; private set; }

    /// <summary>Move dynamic system prompt sections into first user message (Claude --exclude-dynamic-system-prompt-sections).</summary>
    protected bool CurrentExcludeDynamicSystemPromptSections { get; private set; }

    /// <summary>Enable 1-hour prompt cache TTL via ENABLE_PROMPT_CACHING_1H (Claude).</summary>
    protected bool CurrentEnableOneHourPromptCache { get; private set; }

    /// <summary>Skip waiting for MCP connection in -p mode via MCP_CONNECTION_NONBLOCKING (Claude).</summary>
    protected bool CurrentNonBlockingMcpConnection { get; private set; }

    /// <summary>Do not persist the session to disk (Claude --no-session-persistence).</summary>
    protected bool CurrentNoSessionPersistence { get; private set; }

    /// <summary>JSON Schema constraint for structured output (Claude --json-schema).</summary>
    protected string? CurrentJsonSchema { get; private set; }

    /// <summary>Session display name (Claude --name).</summary>
    protected string? CurrentSessionName { get; private set; }

    /// <summary>Include hook lifecycle events in stream-json output (Claude --include-hook-events).</summary>
    protected bool CurrentIncludeHookEvents { get; private set; }

    /// <summary>Path to file appended to the system prompt (Claude --append-system-prompt-file).</summary>
    protected string? CurrentAppendSystemPromptFile { get; private set; }

    /// <summary>Copilot execution mode (Copilot --mode: autopilot/plan/interactive).</summary>
    protected string? CurrentCopilotMode { get; private set; }

    /// <summary>Skip auto-discovered instructions (Copilot --no-custom-instructions).</summary>
    protected bool CurrentDisableCustomInstructions { get; private set; }

    /// <summary>Disable the ask-user tool (Copilot --no-ask-user).</summary>
    protected bool CurrentDisableAskUser { get; private set; }

    /// <summary>Env var names whose values should be redacted (Copilot --secret-env-vars).</summary>
    protected List<string>? CurrentSecretEnvVars { get; private set; }

    /// <summary>MCP servers to disable (Copilot --disable-mcp-server).</summary>
    protected List<string>? CurrentDisabledMcpServers { get; private set; }

    /// <summary>Disable all built-in MCPs including GitHub (Copilot --disable-builtin-mcps).</summary>
    protected bool CurrentDisableBuiltinMcps { get; private set; }

    /// <summary>Allowed URL hosts/patterns (Copilot --allow-url).</summary>
    protected List<string>? CurrentAllowedUrls { get; private set; }

    /// <summary>Denied URL hosts/patterns (Copilot --deny-url).</summary>
    protected List<string>? CurrentDeniedUrls { get; private set; }

    /// <summary>GitHub MCP toolsets to enable (Copilot --add-github-mcp-toolset).</summary>
    protected List<string>? CurrentGitHubMcpToolsets { get; private set; }

    /// <summary>GitHub MCP tools to enable (Copilot --add-github-mcp-tool).</summary>
    protected List<string>? CurrentGitHubMcpTools { get; private set; }

    /// <summary>Toggle streaming output (Copilot --stream on/off).</summary>
    protected bool? CurrentStreamOutput { get; private set; }

    /// <summary>Skip OpenCode permission prompts (OpenCode --dangerously-skip-permissions).</summary>
    protected bool CurrentSkipPermissions { get; private set; }

    /// <summary>Show thinking blocks in OpenCode output (OpenCode --thinking).</summary>
    protected bool CurrentShowThinking { get; private set; }

    /// <summary>
    /// Applies execution options to the current context so that BuildCliArgs can read them.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal void ApplyOptions(ExecutionOptions options)
    {
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
		CurrentUseBareMode = options.UseBareMode;
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
        CurrentReasoningEffort = options.ReasoningEffort;
        CurrentDisableLargeContext = options.DisableLargeContext;
        CurrentBashEnvPath = options.BashEnvPath;
        CurrentPermissionMode = options.PermissionMode;
        CurrentPreassignedSessionId = options.PreassignedSessionId;
        CurrentFallbackModel = options.FallbackModel;
        CurrentStrictMcpConfig = options.StrictMcpConfig;
        CurrentSettingSources = options.SettingSources;
        CurrentExcludeDynamicSystemPromptSections = options.ExcludeDynamicSystemPromptSections;
        CurrentEnableOneHourPromptCache = options.EnableOneHourPromptCache;
        CurrentNonBlockingMcpConnection = options.NonBlockingMcpConnection;
        CurrentNoSessionPersistence = options.NoSessionPersistence;
        CurrentJsonSchema = options.JsonSchema;
        CurrentSessionName = options.SessionName;
        CurrentIncludeHookEvents = options.IncludeHookEvents;
        CurrentAppendSystemPromptFile = options.AppendSystemPromptFile;
        CurrentCopilotMode = options.CopilotMode;
        CurrentDisableCustomInstructions = options.DisableCustomInstructions;
        CurrentDisableAskUser = options.DisableAskUser;
        CurrentSecretEnvVars = options.SecretEnvVars;
        CurrentDisabledMcpServers = options.DisabledMcpServers;
        CurrentDisableBuiltinMcps = options.DisableBuiltinMcps;
        CurrentAllowedUrls = options.AllowedUrls;
        CurrentDeniedUrls = options.DeniedUrls;
        CurrentGitHubMcpToolsets = options.GitHubMcpToolsets;
        CurrentGitHubMcpTools = options.GitHubMcpTools;
        CurrentStreamOutput = options.StreamOutput;
        CurrentSkipPermissions = options.SkipPermissions;
        CurrentShowThinking = options.ShowThinking;
    }

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
		CurrentUseBareMode = false;
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
        CurrentReasoningEffort = null;
        CurrentDisableLargeContext = false;
        CurrentBashEnvPath = null;
        CurrentPermissionMode = null;
        CurrentPreassignedSessionId = null;
        CurrentFallbackModel = null;
        CurrentStrictMcpConfig = false;
        CurrentSettingSources = null;
        CurrentExcludeDynamicSystemPromptSections = false;
        CurrentEnableOneHourPromptCache = false;
        CurrentNonBlockingMcpConnection = false;
        CurrentNoSessionPersistence = false;
        CurrentJsonSchema = null;
        CurrentSessionName = null;
        CurrentIncludeHookEvents = false;
        CurrentAppendSystemPromptFile = null;
        CurrentCopilotMode = null;
        CurrentDisableCustomInstructions = false;
        CurrentDisableAskUser = false;
        CurrentSecretEnvVars = null;
        CurrentDisabledMcpServers = null;
        CurrentDisableBuiltinMcps = false;
        CurrentAllowedUrls = null;
        CurrentDeniedUrls = null;
        CurrentGitHubMcpToolsets = null;
        CurrentGitHubMcpTools = null;
        CurrentStreamOutput = null;
        CurrentSkipPermissions = false;
        CurrentShowThinking = false;
    }

    /// <summary>
    /// Normalizes a provider-specific reasoning effort value against an allow-list.
    /// Returns null when the supplied value is blank or unsupported.
    /// </summary>
    protected static string? NormalizeReasoningEffort(string? reasoningEffort, params string[] allowedValues)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        var normalized = reasoningEffort.Trim().ToLowerInvariant();
        foreach (var allowedValue in allowedValues)
        {
            if (string.Equals(normalized, allowedValue, StringComparison.OrdinalIgnoreCase))
            {
                return allowedValue;
            }
        }

        return null;
    }

    /// <summary>
    /// Merges provider-level environment variables with per-execution overrides.
    /// Per-execution values win when the same key is present in both dictionaries.
    /// </summary>
    protected Dictionary<string, string>? GetEffectiveEnvironmentVariables()
    {
        if ((BaseEnvironmentVariables == null || BaseEnvironmentVariables.Count == 0) &&
            (CurrentEnvironmentVariables == null || CurrentEnvironmentVariables.Count == 0))
        {
            return null;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (BaseEnvironmentVariables != null)
        {
            foreach (var kvp in BaseEnvironmentVariables)
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        if (CurrentEnvironmentVariables != null)
        {
            foreach (var kvp in CurrentEnvironmentVariables)
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        return merged;
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
