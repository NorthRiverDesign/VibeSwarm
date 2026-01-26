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
	/// Creates a successful result.
	/// </summary>
	public static GitOperationResult Succeeded(
		string? output = null,
		string? commitHash = null,
		string? branchName = null,
		string? remoteName = null)
	{
		return new GitOperationResult
		{
			Success = true,
			Output = output,
			CommitHash = commitHash,
			BranchName = branchName,
			RemoteName = remoteName
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
