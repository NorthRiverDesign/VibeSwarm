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

    public abstract Task<ProviderInfo> GetProviderInfoAsync(CancellationToken cancellationToken = default);

    public abstract Task<UsageLimits> GetUsageLimitsAsync(CancellationToken cancellationToken = default);
}
