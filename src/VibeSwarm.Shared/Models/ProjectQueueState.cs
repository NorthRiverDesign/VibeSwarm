namespace VibeSwarm.Shared.Models;

/// <summary>
/// The queue as one project sees it: whether it may start the project's next job, and
/// how many of the project's jobs are waiting or running.
/// </summary>
public sealed class ProjectQueueState
{
	public Guid ProjectId { get; set; }

	/// <summary>The whole queue is paused, so nothing starts for any project.</summary>
	public bool IsQueuePaused { get; set; }

	/// <summary>This project's queued jobs are held back, e.g. while one is being edited.</summary>
	public bool IsProjectPaused { get; set; }

	/// <summary>When the project hold lapses unless it is renewed.</summary>
	public DateTime? ProjectPausedUntil { get; set; }

	/// <summary>The project's jobs that have been picked up and not finished.</summary>
	public int RunningJobs { get; set; }

	/// <summary>The project's jobs waiting to start.</summary>
	public int QueuedJobs { get; set; }

	/// <summary>Whether a queued job of this project may be started right now.</summary>
	public bool CanStartJobs => !IsQueuePaused && !IsProjectPaused;
}
