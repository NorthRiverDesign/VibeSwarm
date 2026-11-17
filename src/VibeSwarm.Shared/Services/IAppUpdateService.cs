using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Checks GitHub for a newer release and installs it. Installing stages the release beside the
/// running one, waits for running jobs to finish, then restarts through the installer's launcher,
/// which switches versions and rolls back if the new one doesn't start.
/// </summary>
public interface IAppUpdateService
{
	Task<AppUpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default);

	Task<AppUpdateStatus> CheckNowAsync(CancellationToken cancellationToken = default);

	/// <summary>Starts installing the latest release. Refusals come back in <see cref="AppUpdateStatus.StageMessage"/>.</summary>
	Task<AppUpdateStatus> StartUpdateAsync(CancellationToken cancellationToken = default);

	/// <summary>Stops an update that is still downloading or waiting for jobs.</summary>
	Task<AppUpdateStatus> CancelUpdateAsync(CancellationToken cancellationToken = default);
}
