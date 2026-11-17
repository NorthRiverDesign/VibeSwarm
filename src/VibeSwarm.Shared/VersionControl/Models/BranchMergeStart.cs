namespace VibeSwarm.Shared.VersionControl.Models;

public enum BranchMergeOutcome
{
	/// <summary>The branch is already part of the checkout's history; there is nothing to merge.</summary>
	AlreadyMerged,

	/// <summary>The merge is in progress with nothing left to resolve.</summary>
	Merged,

	/// <summary>The merge is in progress and <see cref="BranchMergeStart.ConflictedFiles"/> hold conflict markers.</summary>
	Conflicted,

	Failed
}

public sealed record BranchMergeStart(BranchMergeOutcome Outcome, string? SourceRef, IReadOnlyList<string> ConflictedFiles, string? Error)
{
	public static BranchMergeStart Failed(string error) => new(BranchMergeOutcome.Failed, null, [], error);
}
