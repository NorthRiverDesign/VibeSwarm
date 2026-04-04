using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public interface IDeveloperModeService
{
	Task<DeveloperModeStatus> GetStatusAsync(CancellationToken cancellationToken = default);
	Task<DeveloperModeStatus> StartSelfUpdateAsync(CancellationToken cancellationToken = default);
}
