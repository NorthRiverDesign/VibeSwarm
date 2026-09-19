using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// The stop switch for unattended operation. Pausing is persisted, so it survives a
/// restart and cannot be undone by a worker coming back up.
/// </summary>
public interface IJobQueueControlService
{
	Task<JobQueueState> GetStateAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Stops the queue starting anything else. With <paramref name="cancelRunningJobs"/>
	/// the jobs already executing are asked to stop as well, which is the difference
	/// between "let it finish" and "stop now".
	/// </summary>
	Task<JobQueueState> PauseAsync(
		string? reason = null,
		bool cancelRunningJobs = false,
		CancellationToken cancellationToken = default);

	Task<JobQueueState> ResumeAsync(CancellationToken cancellationToken = default);
}
