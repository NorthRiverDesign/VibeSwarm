using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public interface ICommonProviderSetupService
{
	Task<IReadOnlyList<CommonProviderSetupStatus>> GetStatusesAsync(CancellationToken cancellationToken = default);
	Task<IReadOnlyList<CommonProviderSetupStatus>> RefreshAsync(CancellationToken cancellationToken = default);
	Task<CommonProviderActionResult> InstallAsync(ProviderType providerType, CancellationToken cancellationToken = default);
	Task<CommonProviderActionResult> SaveAuthenticationAsync(CommonProviderSetupRequest request, CancellationToken cancellationToken = default);
}
