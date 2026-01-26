using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public interface IProviderService
{
    Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default);
    Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default);
    Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default);
    Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a provider instance from a provider configuration.
    /// Useful for calling provider-specific methods like GetSessionSummaryAsync.
    /// </summary>
    IProvider? CreateInstance(Provider config);

    /// <summary>
    /// Gets the session summary for a completed job.
    /// </summary>
    Task<SessionSummary> GetSessionSummaryAsync(
        Guid providerId,
        string? sessionId,
        string? workingDirectory = null,
        string? fallbackOutput = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the available models for a provider from the database.
    /// </summary>
    Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the available models for a provider by querying the provider and updating the database.
    /// </summary>
    Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default);
}

public class ConnectionTestResult
{
    public bool IsConnected { get; set; }
    public string? ErrorMessage { get; set; }
}
