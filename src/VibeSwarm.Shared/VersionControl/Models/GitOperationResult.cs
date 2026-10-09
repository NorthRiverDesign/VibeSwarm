namespace VibeSwarm.Shared.VersionControl.Models;

public sealed class GitOperationResult
{
	public bool Success { get; init; }

	public string? Error { get; init; }

	public string? Output { get; init; }

	public string? CommitHash { get; init; }

	public string? BranchName { get; init; }

	public string? RemoteName { get; init; }

	public string? TargetBranch { get; init; }

	public string? PullRequestUrl { get; init; }

	public int? PullRequestNumber { get; init; }

	/// <summary>
	/// Saved reference for preserved changes, such as a stash reference.
	/// </summary>
	public string? SavedReference { get; init; }

	public int? ChangedFilesCount { get; init; }

	/// <summary>
	/// Local commits that were not on the remote yet and were replayed on top of it by a sync.
	/// </summary>
	public int? KeptLocalCommits { get; init; }

	/// <summary>
	/// Branch that holds local commits a sync could not replay on top of the remote.
	/// </summary>
	public string? RecoveryBranch { get; init; }

	/// <summary>
	/// Merge conflict files captured during merge preview or resolution.
	/// </summary>
	public IReadOnlyList<MergeConflictFile> MergeConflictFiles { get; init; } = [];

	public static GitOperationResult Succeeded(
		string? output = null,
		string? commitHash = null,
		string? branchName = null,
		string? remoteName = null,
		string? targetBranch = null,
		string? pullRequestUrl = null,
		int? pullRequestNumber = null,
		string? savedReference = null,
		int? changedFilesCount = null,
		IReadOnlyList<MergeConflictFile>? mergeConflictFiles = null,
		int? keptLocalCommits = null,
		string? recoveryBranch = null)
	{
		return new GitOperationResult
		{
			Success = true,
			Output = output,
			CommitHash = commitHash,
			BranchName = branchName,
			RemoteName = remoteName,
			TargetBranch = targetBranch,
			PullRequestUrl = pullRequestUrl,
			PullRequestNumber = pullRequestNumber,
			SavedReference = savedReference,
			ChangedFilesCount = changedFilesCount,
			MergeConflictFiles = mergeConflictFiles ?? [],
			KeptLocalCommits = keptLocalCommits,
			RecoveryBranch = recoveryBranch
		};
	}

	public static GitOperationResult Failed(
		string error,
		string? commitHash = null,
		IReadOnlyList<MergeConflictFile>? mergeConflictFiles = null)
	{
		return new GitOperationResult
		{
			Success = false,
			Error = error,
			CommitHash = commitHash,
			MergeConflictFiles = mergeConflictFiles ?? []
		};
	}
}
