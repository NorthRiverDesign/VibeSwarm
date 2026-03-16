namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// Result of a git operation (commit, push, etc.).
/// </summary>
public sealed class GitOperationResult
{
	/// <summary>
	/// Whether the operation succeeded.
	/// </summary>
	public bool Success { get; init; }

	/// <summary>
	/// Error message if the operation failed.
	/// </summary>
	public string? Error { get; init; }

	/// <summary>
	/// Output from the git command.
	/// </summary>
	public string? Output { get; init; }

	/// <summary>
	/// Commit hash if a commit was created.
	/// </summary>
	public string? CommitHash { get; init; }

	/// <summary>
	/// Branch name involved in the operation.
	/// </summary>
	public string? BranchName { get; init; }

	/// <summary>
	/// Remote name involved in the operation.
	/// </summary>
	public string? RemoteName { get; init; }

	/// <summary>
	/// Target branch involved in the operation.
	/// </summary>
	public string? TargetBranch { get; init; }

	/// <summary>
	/// Created pull request URL, if applicable.
	/// </summary>
	public string? PullRequestUrl { get; init; }

	/// <summary>
	/// Created pull request number, if applicable.
	/// </summary>
	public int? PullRequestNumber { get; init; }

	/// <summary>
	/// Saved reference for preserved changes, such as a stash reference.
	/// </summary>
	public string? SavedReference { get; init; }

	/// <summary>
	/// Number of changed files involved in the operation, when known.
	/// </summary>
	public int? ChangedFilesCount { get; init; }

	/// <summary>
	/// Creates a successful result.
	/// </summary>
	public static GitOperationResult Succeeded(
		string? output = null,
		string? commitHash = null,
		string? branchName = null,
		string? remoteName = null,
		string? targetBranch = null,
		string? pullRequestUrl = null,
		int? pullRequestNumber = null,
		string? savedReference = null,
		int? changedFilesCount = null)
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
			ChangedFilesCount = changedFilesCount
		};
	}

	/// <summary>
	/// Creates a failed result.
	/// </summary>
	public static GitOperationResult Failed(string error, string? commitHash = null)
	{
		return new GitOperationResult
		{
			Success = false,
			Error = error,
			CommitHash = commitHash
		};
	}
}
