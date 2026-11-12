using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

/// <summary>
/// Why a queued job has not started yet, read from the same rules the scheduler applies.
/// </summary>
public sealed class JobQueueStatus
{
	public JobQueueWaitReason Reason { get; set; }

	/// <summary>The job this one is waiting for, when one job holds it back.</summary>
	public Guid? BlockingJobId { get; set; }

	public string? BlockingJobTitle { get; set; }

	public string? BlockingProjectName { get; set; }

	public JobStatus? BlockingJobStatus { get; set; }

	/// <summary>The provider this job will run on.</summary>
	public string? ProviderName { get; set; }

	/// <summary>When a timed hold or an edit hold lapses.</summary>
	public DateTime? WaitUntilUtc { get; set; }

	/// <summary>
	/// Queued jobs that will start before this one because they share its project or provider
	/// and come first in the queue order (higher priority, then older).
	/// </summary>
	public int JobsAhead { get; set; }
}

public enum JobQueueWaitReason
{
	/// <summary>Nothing holds it back: the queue starts it on its next pass.</summary>
	StartingSoon,
	/// <summary>The whole queue is stopped.</summary>
	QueuePaused,
	/// <summary>
	/// Held back until <see cref="JobQueueStatus.WaitUntilUtc"/>: a usage limit or provider
	/// cooldown resetting, or a retry while origin was unreachable.
	/// </summary>
	HeldUntil,
	/// <summary>Runs only after another job completes.</summary>
	WaitingOnDependency,
	/// <summary>A queued job in the same project is open for editing.</summary>
	ProjectEditHold,
	/// <summary>Another job is using the project's checkout.</summary>
	ProjectBusy,
	/// <summary>The provider is busy with a job, usually in another project.</summary>
	ProviderBusy
}
