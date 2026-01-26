using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Service for interacting with version control systems.
/// Currently supports Git repositories.
/// </summary>
public interface IVersionControlService
{
	/// <summary>
	/// Checks if git is available on the system.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>True if git is available.</returns>
	Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks if the specified directory is a git repository.
	/// </summary>
	/// <param name="workingDirectory">The directory to check.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>True if the directory is inside a git repository.</returns>
	Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the current HEAD commit hash for a repository.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The commit hash, or null if not available.</returns>
	Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the current branch name.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The branch name, or null if not available.</returns>
	Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the remote URL for the specified remote.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="remoteName">The remote name (defaults to 'origin').</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The remote URL, or null if not available.</returns>
	Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks if there are any uncommitted changes in the working directory.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>True if there are uncommitted changes.</returns>
	Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets a list of files that have been changed.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="baseCommit">Optional base commit to compare against (defaults to HEAD).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>List of changed file paths.</returns>
	Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the diff of all changes in the working directory (staged and unstaged).
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="baseCommit">Optional base commit to compare against (defaults to HEAD).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The diff output, or null if not available.</returns>
	Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the diff of changes between two commits.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="fromCommit">The starting commit.</param>
	/// <param name="toCommit">The ending commit (defaults to HEAD).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The diff output, or null if not available.</returns>
	Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets a summary of changed files without full diff content.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="baseCommit">Optional base commit to compare against.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Summary of changes.</returns>
	Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Stages all changes and creates a commit with the specified message.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="commitMessage">The commit message.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status, commit hash, and any error message.</returns>
	Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default);

	/// <summary>
	/// Pushes commits to the remote repository.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="remoteName">The remote name (defaults to 'origin').</param>
	/// <param name="branchName">The branch name (defaults to current branch).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status and any error message.</returns>
	Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Commits all changes and pushes to the remote repository in a single operation.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="commitMessage">The commit message.</param>
	/// <param name="remoteName">The remote name (defaults to 'origin').</param>
	/// <param name="progressCallback">Optional callback for progress updates.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status and any error message.</returns>
	Task<GitOperationResult> CommitAndPushAsync(
		string workingDirectory,
		string commitMessage,
		string remoteName = "origin",
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default);
}
