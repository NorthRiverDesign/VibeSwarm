namespace VibeSwarm.Shared.Models;

/// <summary>
/// What the queue is doing right now, and whether it is allowed to start anything.
/// </summary>
public sealed class JobQueueState
{
	/// <summary>No further jobs will be started while this is set.</summary>
	public bool IsPaused { get; set; }

	/// <summary>Why it was paused — shown wherever the paused state appears.</summary>
	public string? PausedReason { get; set; }

	public DateTime? PausedAt { get; set; }

	/// <summary>Jobs executing right now. A pause does not touch these unless asked.</summary>
	public int RunningJobs { get; set; }

	/// <summary>Jobs waiting for a slot.</summary>
	public int PendingJobs { get; set; }

	/// <summary>How many running jobs the last pause asked to stop.</summary>
	public int CancelledJobs { get; set; }
}
