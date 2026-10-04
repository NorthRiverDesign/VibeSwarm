using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Captures the change-set snapshot of a job before it is reset for a follow-up.
/// One record is saved per follow-up continuation so users can review every
/// commit / diff produced across all follow-up iterations of the same job, and so
/// the follow-up can re-apply the work from <see cref="WorkSnapshotCommit"/>.
/// </summary>
public class JobChangeSet
{
	public Guid Id { get; set; }

	public Guid JobId { get; set; }
	public Job? Job { get; set; }

	/// <summary>
	/// 0-based index. 0 = initial run, 1 = first follow-up, etc.
	/// </summary>
	public int FollowUpIndex { get; set; }

	public DateTime? CompletedAt { get; set; }

	[StringLength(100)]
	public string? GitCommitHash { get; set; }

	[StringLength(100)]
	public string? GitCommitBefore { get; set; }

	public int? ChangedFilesCount { get; set; }

	/// <summary>
	/// The diff the run ended with, kept because the follow-up clears the job's own.
	/// </summary>
	public string? GitDiff { get; set; }

	/// <summary>
	/// Commit holding the job's accumulated work at the end of this run, kept under
	/// <c>refs/vibeswarm/jobs/</c> so the pre-run reset of a later job cannot drop it.
	/// </summary>
	[StringLength(100)]
	public string? WorkSnapshotCommit { get; set; }

	public string? SessionSummary { get; set; }

	public int? PullRequestNumber { get; set; }

	[StringLength(500)]
	public string? PullRequestUrl { get; set; }

	public DateTime? MergedAt { get; set; }

	public bool? BuildVerified { get; set; }

	[StringLength(200)]
	public string? ModelUsed { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
