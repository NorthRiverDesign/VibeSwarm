namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// What happened when a follow-up re-applied the work its job's earlier runs left behind.
/// </summary>
public enum JobWorkRestoreOutcome
{
	/// <summary>The earlier work is already on the checked-out branch.</summary>
	AlreadyOnBranch,

	/// <summary>The earlier work was re-applied cleanly as uncommitted changes.</summary>
	Restored,

	/// <summary>The earlier work was re-applied, but some files carry conflict markers.</summary>
	Conflicted,

	/// <summary>The saved work could not be re-applied.</summary>
	Failed
}

public sealed class JobWorkRestoreResult
{
	public JobWorkRestoreOutcome Outcome { get; init; }

	/// <summary>Files that differ from the branch after the restore.</summary>
	public IReadOnlyList<string> Files { get; init; } = [];

	/// <summary>Files left with conflict markers for the agent to resolve.</summary>
	public IReadOnlyList<string> ConflictedFiles { get; init; } = [];

	public string? Error { get; init; }

	public static JobWorkRestoreResult AlreadyOnBranch() => new() { Outcome = JobWorkRestoreOutcome.AlreadyOnBranch };

	public static JobWorkRestoreResult Failed(string error) => new() { Outcome = JobWorkRestoreOutcome.Failed, Error = error };
}
