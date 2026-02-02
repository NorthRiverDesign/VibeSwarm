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

	/// <summary>
	/// Gets all branches (local and remote) for a repository.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="includeRemote">Whether to include remote tracking branches.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>List of branch information.</returns>
	Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default);

	/// <summary>
	/// Fetches updates from the remote repository.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="remoteName">The remote name (defaults to 'origin').</param>
	/// <param name="prune">Whether to prune deleted remote branches.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status and any error message.</returns>
	Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default);

	/// <summary>
	/// Performs a hard checkout to a branch, discarding all local changes.
	/// This is equivalent to: git fetch, git checkout branch, git reset --hard origin/branch
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="branchName">The branch name to checkout.</param>
	/// <param name="remoteName">The remote name (defaults to 'origin').</param>
	/// <param name="progressCallback">Optional callback for progress updates.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status and any error message.</returns>
	Task<GitOperationResult> HardCheckoutBranchAsync(
		string workingDirectory,
		string branchName,
		string remoteName = "origin",
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Syncs the current branch with the remote, discarding all local changes.
	/// This is equivalent to: git fetch origin, git reset --hard origin/current-branch
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="remoteName">The remote name (defaults to 'origin').</param>
	/// <param name="progressCallback">Optional callback for progress updates.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status and any error message.</returns>
	Task<GitOperationResult> SyncWithOriginAsync(
		string workingDirectory,
		string remoteName = "origin",
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Clones a repository from a remote URL to a local directory.
	/// </summary>
	/// <param name="repositoryUrl">The URL of the repository to clone (e.g., https://github.com/owner/repo.git or git@github.com:owner/repo.git).</param>
	/// <param name="targetDirectory">The local directory to clone into. The directory should not exist or be empty.</param>
	/// <param name="branch">Optional branch name to checkout after cloning.</param>
	/// <param name="progressCallback">Optional callback for progress updates.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status and any error message.</returns>
	Task<GitOperationResult> CloneRepositoryAsync(
		string repositoryUrl,
		string targetDirectory,
		string? branch = null,
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Converts a GitHub "owner/repo" format string to a clone URL.
	/// </summary>
	/// <param name="ownerAndRepo">The owner/repo string (e.g., "microsoft/vscode").</param>
	/// <param name="useSsh">If true, returns an SSH URL (git@github.com:owner/repo.git). If false, returns HTTPS URL.</param>
	/// <returns>The clone URL.</returns>
	string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true);

	/// <summary>
	/// Extracts the "owner/repo" format from a Git remote URL.
	/// Supports both SSH (git@github.com:owner/repo.git) and HTTPS (https://github.com/owner/repo.git) URLs.
	/// </summary>
	/// <param name="remoteUrl">The Git remote URL.</param>
	/// <returns>The owner/repo string (e.g., "microsoft/vscode"), or null if the URL is not a valid GitHub URL.</returns>
	string? ExtractGitHubRepository(string? remoteUrl);

	/// <summary>
	/// Creates a new local branch from the current HEAD. This branch is not pushed to the remote.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="branchName">The name of the new branch to create.</param>
	/// <param name="switchToBranch">If true, switches to the new branch after creation (default: true).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status, branch name, and any error message.</returns>
	Task<GitOperationResult> CreateBranchAsync(
		string workingDirectory,
		string branchName,
		bool switchToBranch = true,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Discards all uncommitted changes in the working directory.
	/// This includes staged changes, unstaged modifications, and optionally untracked files.
	/// Equivalent to: git reset --hard HEAD && git clean -fd
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="includeUntracked">If true, also removes untracked files (default: true).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Result containing success status and any error message.</returns>
	Task<GitOperationResult> DiscardAllChangesAsync(
		string workingDirectory,
		bool includeUntracked = true,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the commit log messages between two commits.
	/// Used to retrieve commit messages made by an AI agent during job execution.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory.</param>
	/// <param name="fromCommit">The starting commit (exclusive).</param>
	/// <param name="toCommit">The ending commit (inclusive, defaults to HEAD).</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>List of commit messages (subject line only from --oneline format).</returns>
	Task<IReadOnlyList<string>> GetCommitLogAsync(
		string workingDirectory,
		string fromCommit,
		string? toCommit = null,
		CancellationToken cancellationToken = default);
}
