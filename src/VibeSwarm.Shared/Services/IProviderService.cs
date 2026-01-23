using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public interface IProviderService
{
    Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default);
    Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default);
}

public class ConnectionTestResult
{
    public bool IsConnected { get; set; }
    public string? ErrorMessage { get; set; }
}
