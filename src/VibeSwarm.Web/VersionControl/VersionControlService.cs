using System.Text;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Service for interacting with Git version control.
/// </summary>
public sealed class VersionControlService : IVersionControlService
{
	private const int DefaultTimeoutSeconds = 30;
	private const int MaxDiffSizeBytes = 1024 * 1024; // 1 MB max diff size

	private readonly IGitCommandExecutor _commandExecutor;
	private readonly ILogger<VersionControlService> _logger;

	public VersionControlService(IGitCommandExecutor commandExecutor, ILogger<VersionControlService> logger)
	{
		_commandExecutor = commandExecutor;
		_logger = logger;
	}

	/// <inheritdoc />
	public async Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"--version",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 5);

			return result.Success && result.Output.Contains("git version");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check git availability");
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"rev-parse --is-inside-work-tree",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 5);

			return result.Success && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check if {Directory} is a git repository", workingDirectory);
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"rev-parse HEAD",
				workingDirectory,
				cancellationToken);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return result.Output.Trim();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get current commit hash for {Directory}", workingDirectory);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"rev-parse --abbrev-ref HEAD",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return result.Output.Trim();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get current branch for {Directory}", workingDirectory);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				$"remote get-url {remoteName}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return result.Output.Trim();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get remote URL for {Directory}", workingDirectory);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var status = await GetWorkingTreeStatusAsync(workingDirectory, cancellationToken);
			return status.HasUncommittedChanges;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check uncommitted changes for {Directory}", workingDirectory);
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"status --porcelain=v1 --untracked-files=all",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (!result.Success)
			{
				return new GitWorkingTreeStatus();
			}

			var changedFiles = ParseWorkingTreeStatus(result.Output);
			if (changedFiles.Count == 0 && !string.IsNullOrWhiteSpace(result.Output))
			{
				changedFiles = (await GetChangedFilesAsync(workingDirectory, cancellationToken: cancellationToken)).ToList();
			}

			return new GitWorkingTreeStatus
			{
				HasUncommittedChanges = !string.IsNullOrWhiteSpace(result.Output),
				ChangedFiles = changedFiles,
				ChangedFilesCount = changedFiles.Count
			};
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get working tree status for {Directory}", workingDirectory);
			return new GitWorkingTreeStatus();
		}
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default)
	{
		try
		{
			var diffTarget = string.IsNullOrEmpty(baseCommit) ? "HEAD" : baseCommit;
			var changedFiles = new List<string>();

			// Get tracked file changes (modified, deleted, renamed) vs HEAD
			var result = await _commandExecutor.ExecuteAsync(
				$"diff {diffTarget} --name-only",
				workingDirectory,
				cancellationToken);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				changedFiles.AddRange(result.Output
					.Split('\n', StringSplitOptions.RemoveEmptyEntries)
					.Select(f => f.Trim())
					.Where(f => !string.IsNullOrEmpty(f)));
			}

			// If diff against HEAD failed (e.g., fresh repo with no commits), fall back to staged files
			if (!result.Success)
			{
				var stagedResult = await _commandExecutor.ExecuteAsync(
					"diff --cached --name-only",
					workingDirectory,
					cancellationToken);

				if (stagedResult.Success && !string.IsNullOrWhiteSpace(stagedResult.Output))
				{
					changedFiles.AddRange(stagedResult.Output
						.Split('\n', StringSplitOptions.RemoveEmptyEntries)
						.Select(f => f.Trim())
						.Where(f => !string.IsNullOrEmpty(f)));
				}
			}

			// Get untracked files (new files not yet added to Git)
			var untrackedResult = await _commandExecutor.ExecuteAsync(
				"ls-files --others --exclude-standard",
				workingDirectory,
				cancellationToken);

			if (untrackedResult.Success && !string.IsNullOrWhiteSpace(untrackedResult.Output))
			{
				changedFiles.AddRange(untrackedResult.Output
					.Split('\n', StringSplitOptions.RemoveEmptyEntries)
					.Select(f => f.Trim())
					.Where(f => !string.IsNullOrEmpty(f)));
			}

			return changedFiles.Distinct().ToList();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get changed files for {Directory} (baseCommit: {BaseCommit})", workingDirectory, baseCommit);
		}

		return Array.Empty<string>();
	}

	/// <inheritdoc />
	public async Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default)
	{
		try
		{
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return null;
			}

			var diffTarget = string.IsNullOrEmpty(baseCommit) ? "HEAD" : baseCommit;
			var diffParts = new List<string>();

			// Get diff for tracked changes (modified + deleted)
			var result = await _commandExecutor.ExecuteAsync(
				$"diff {diffTarget} --stat --patch --find-renames --find-copies",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 60);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				diffParts.Add(result.Output);
			}

			// If diff against commit fails (e.g., no commits yet), try diff against empty tree
			if (!result.Success)
			{
				var emptyTreeResult = await _commandExecutor.ExecuteAsync(
					"diff --cached --stat --patch",
					workingDirectory,
					cancellationToken);

				if (emptyTreeResult.Success && !string.IsNullOrWhiteSpace(emptyTreeResult.Output))
				{
					diffParts.Add(emptyTreeResult.Output);
				}
			}

			// Get untracked files and generate synthetic diff for them
			var untrackedResult = await _commandExecutor.ExecuteAsync(
				"ls-files --others --exclude-standard",
				workingDirectory,
				cancellationToken);

			if (untrackedResult.Success && !string.IsNullOrWhiteSpace(untrackedResult.Output))
			{
				var untrackedFiles = untrackedResult.Output
					.Split('\n', StringSplitOptions.RemoveEmptyEntries)
					.Select(f => f.Trim())
					.Where(f => !string.IsNullOrEmpty(f))
					.ToList();

				foreach (var file in untrackedFiles)
				{
					// Use git diff --no-index to generate a proper diff for untracked files
					var fileDiffResult = await _commandExecutor.ExecuteAsync(
						$"diff --no-index -- /dev/null \"{file}\"",
						workingDirectory,
						cancellationToken,
						timeoutSeconds: 10);

					// git diff --no-index returns exit code 1 when files differ (which is expected)
					// so we check the output rather than success
					if (!string.IsNullOrWhiteSpace(fileDiffResult.Output))
					{
						diffParts.Add(fileDiffResult.Output);
					}
					else
					{
						// Fallback: generate a minimal synthetic diff header
						diffParts.Add($"diff --git a/{file} b/{file}\nnew file mode 100644\n--- /dev/null\n+++ b/{file}");
					}
				}
			}

			if (diffParts.Count == 0)
			{
				return null;
			}

			var combinedDiff = string.Join("\n", diffParts);

			if (!string.IsNullOrEmpty(combinedDiff) && Encoding.UTF8.GetByteCount(combinedDiff) > MaxDiffSizeBytes)
			{
				combinedDiff = TruncateDiff(combinedDiff, MaxDiffSizeBytes);
			}

			return string.IsNullOrWhiteSpace(combinedDiff) ? null : combinedDiff;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get working directory diff for {Directory} (baseCommit: {BaseCommit})", workingDirectory, baseCommit);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default)
	{
		try
		{
			var targetCommit = string.IsNullOrEmpty(toCommit) ? "HEAD" : toCommit;
			var result = await _commandExecutor.ExecuteAsync(
				$"diff {fromCommit}..{targetCommit} --stat --patch --find-renames --find-copies",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 60);

			if (result.Success)
			{
				var diff = result.Output;

				if (!string.IsNullOrEmpty(diff) && Encoding.UTF8.GetByteCount(diff) > MaxDiffSizeBytes)
				{
					diff = TruncateDiff(diff, MaxDiffSizeBytes);
				}

				return string.IsNullOrWhiteSpace(diff) ? null : diff;
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get commit range diff for {Directory} (from: {FromCommit})", workingDirectory, fromCommit);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default)
	{
		try
		{
			var diffTarget = string.IsNullOrEmpty(baseCommit) ? "HEAD" : baseCommit;
			var result = await _commandExecutor.ExecuteAsync(
				$"diff {diffTarget} --stat --shortstat",
				workingDirectory,
				cancellationToken);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return ParseDiffSummary(result.Output);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get diff summary for {Directory} (baseCommit: {BaseCommit})", workingDirectory, baseCommit);
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default)
	{
		try
		{
			// Stage all changes
			var addResult = await _commandExecutor.ExecuteAsync(
				"add -A",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (!addResult.Success)
			{
				return GitOperationResult.Failed($"Failed to stage changes: {addResult.Error}");
			}

			// Escape the commit message for command line
			var escapedMessage = commitMessage
				.Replace("\\", "\\\\")
				.Replace("\"", "\\\"");

			// Create the commit
			var commitResult = await _commandExecutor.ExecuteAsync(
				$"commit -m \"{escapedMessage}\"",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (!commitResult.Success)
			{
				if (commitResult.Output.Contains("nothing to commit") ||
					commitResult.Error.Contains("nothing to commit"))
				{
					return GitOperationResult.Failed("Nothing to commit - no changes detected");
				}

				return GitOperationResult.Failed($"Failed to commit: {commitResult.Error}");
			}

			var commitHash = await GetCurrentCommitHashAsync(workingDirectory, cancellationToken);

			return GitOperationResult.Succeeded(
				output: commitResult.Output,
				commitHash: commitHash);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Operation was cancelled");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default)
	{
		try
		{
			// Get current branch if not specified
			if (string.IsNullOrEmpty(branchName))
			{
				branchName = await GetCurrentBranchAsync(workingDirectory, cancellationToken);
				if (string.IsNullOrEmpty(branchName))
				{
					return GitOperationResult.Failed("Could not determine current branch");
				}
			}

			var pushResult = await _commandExecutor.ExecuteAsync(
				$"push {remoteName} {branchName}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 120);

			if (!pushResult.Success)
			{
				var errorMessage = pushResult.Error;

				if (errorMessage.Contains("rejected"))
				{
					return GitOperationResult.Failed("Push was rejected. You may need to pull remote changes first.");
				}

				if (errorMessage.Contains("Permission denied") || errorMessage.Contains("authentication"))
				{
					return GitOperationResult.Failed("Authentication failed. Please check your credentials.");
				}

				if (errorMessage.Contains("remote") && errorMessage.Contains("not found"))
				{
					return GitOperationResult.Failed($"Remote '{remoteName}' not found.");
				}

				return GitOperationResult.Failed($"Push failed: {errorMessage}");
			}

			return GitOperationResult.Succeeded(
				output: !string.IsNullOrWhiteSpace(pushResult.Output)
					? pushResult.Output
					: pushResult.Error, // Git often writes success info to stderr
				branchName: branchName,
				remoteName: remoteName);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Push operation timed out or was cancelled");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> CommitAndPushAsync(
		string workingDirectory,
		string commitMessage,
		string remoteName = "origin",
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			progressCallback?.Invoke("Checking for changes...");

			var hasChanges = await HasUncommittedChangesAsync(workingDirectory, cancellationToken);
			if (!hasChanges)
			{
				return GitOperationResult.Failed("No changes to commit");
			}

			progressCallback?.Invoke("Staging and committing changes...");

			var commitResult = await CommitAllChangesAsync(workingDirectory, commitMessage, cancellationToken);
			if (!commitResult.Success)
			{
				return commitResult;
			}

			progressCallback?.Invoke("Pushing to remote repository...");

			var pushResult = await PushAsync(workingDirectory, remoteName, null, cancellationToken);
			if (!pushResult.Success)
			{
				return GitOperationResult.Failed(
					$"Commit succeeded (hash: {commitResult.CommitHash?[..7] ?? "unknown"}) but push failed: {pushResult.Error}",
					commitHash: commitResult.CommitHash);
			}

			return GitOperationResult.Succeeded(
				output: $"Successfully committed and pushed to {pushResult.RemoteName}/{pushResult.BranchName}",
				commitHash: commitResult.CommitHash,
				branchName: pushResult.BranchName,
				remoteName: pushResult.RemoteName);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Operation was cancelled or timed out");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> CreatePullRequestAsync(
		string workingDirectory,
		string sourceBranch,
		string targetBranch,
		string title,
		string? body = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(sourceBranch))
			{
				return GitOperationResult.Failed("Source branch cannot be empty.");
			}

			if (string.IsNullOrWhiteSpace(targetBranch))
			{
				return GitOperationResult.Failed("Target branch cannot be empty.");
			}

			if (string.Equals(sourceBranch, targetBranch, StringComparison.Ordinal))
			{
				return GitOperationResult.Failed("Source and target branches must be different.");
			}

			if (string.IsNullOrWhiteSpace(title))
			{
				return GitOperationResult.Failed("Pull request title cannot be empty.");
			}

			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return GitOperationResult.Failed("The specified directory is not a git repository.");
			}

			var ghAvailable = await IsGitHubCliAvailableAsync(cancellationToken);
			if (!ghAvailable)
			{
				return GitOperationResult.Failed("GitHub CLI (gh) is not installed. Please install it from https://cli.github.com/");
			}

			var ghAuthenticated = await IsGitHubCliAuthenticatedAsync(cancellationToken);
			if (!ghAuthenticated)
			{
				return GitOperationResult.Failed("Not authenticated with GitHub CLI. Please run 'gh auth login' to authenticate.");
			}

			var pushResult = await PushAsync(workingDirectory, "origin", sourceBranch, cancellationToken);
			if (!pushResult.Success)
			{
				return GitOperationResult.Failed($"Failed to push source branch before creating pull request: {pushResult.Error}");
			}

			var ghArgs = new StringBuilder("pr create");
			ghArgs.Append($" --base \"{EscapeCommandArgument(targetBranch)}\"");
			ghArgs.Append($" --head \"{EscapeCommandArgument(sourceBranch)}\"");
			ghArgs.Append($" --title \"{EscapeCommandArgument(title)}\"");
			ghArgs.Append($" --body \"{EscapeCommandArgument(body ?? string.Empty)}\"");

			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				ghArgs.ToString(),
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 120);

			if (!result.Success)
			{
				var error = BuildCommandError(result, "Failed to create pull request.");
				if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
				{
					return GitOperationResult.Failed("A pull request already exists for these branches.");
				}

				return GitOperationResult.Failed(error);
			}

			var output = string.Join('\n', new[] { result.Output, result.Error }
				.Where(value => !string.IsNullOrWhiteSpace(value)))
				.Trim();
			var pullRequestUrl = ExtractPullRequestUrl(output);
			var pullRequestNumber = ExtractPullRequestNumber(pullRequestUrl);

			return GitOperationResult.Succeeded(
				output: string.IsNullOrWhiteSpace(output) ? "Pull request created successfully." : output,
				branchName: sourceBranch,
				targetBranch: targetBranch,
				pullRequestUrl: pullRequestUrl,
				pullRequestNumber: pullRequestNumber);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Create pull request operation was cancelled or timed out.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error creating pull request: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public Task<GitOperationResult> PreviewMergeBranchAsync(
		string workingDirectory,
		string sourceBranch,
		string targetBranch,
		string remoteName = "origin",
		CancellationToken cancellationToken = default)
		=> ExecuteMergeBranchAsync(
			workingDirectory,
			sourceBranch,
			targetBranch,
			remoteName,
			progressCallback: null,
			cancellationToken,
			previewOnly: true,
			pushAfterMerge: false);

	/// <inheritdoc />
	public Task<GitOperationResult> MergeBranchAsync(
		string workingDirectory,
		string sourceBranch,
		string targetBranch,
		string remoteName = "origin",
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default,
		bool pushAfterMerge = true)
		=> ExecuteMergeBranchAsync(
			workingDirectory,
			sourceBranch,
			targetBranch,
			remoteName,
			progressCallback,
			cancellationToken,
			previewOnly: false,
			pushAfterMerge: pushAfterMerge);

	private async Task<GitOperationResult> ExecuteMergeBranchAsync(
		string workingDirectory,
		string sourceBranch,
		string targetBranch,
		string remoteName,
		Action<string>? progressCallback,
		CancellationToken cancellationToken,
		bool previewOnly,
		bool pushAfterMerge)
	{
		var tempWorktreePath = Path.Combine(Path.GetTempPath(), $"vibeswarm-merge-{Guid.NewGuid():N}");
		var worktreeAdded = false;
		GitOperationResult? operationResult = null;
		string? cleanupFailureMessage = null;

		try
		{
			var validationError = await ValidateMergeRequestAsync(
				workingDirectory,
				sourceBranch,
				targetBranch,
				remoteName,
				cancellationToken);

			if (validationError != null)
			{
				return validationError;
			}

			progressCallback?.Invoke($"Fetching latest from {remoteName}...");
			var fetchResult = await FetchAsync(workingDirectory, remoteName, prune: true, cancellationToken);
			if (!fetchResult.Success)
			{
				return GitOperationResult.Failed(fetchResult.Error ?? "Failed to fetch remote branches.");
			}

			var sourceRef = await ResolveMergeSourceRefAsync(workingDirectory, sourceBranch, remoteName, cancellationToken);
			if (sourceRef == null)
			{
				return GitOperationResult.Failed($"Source branch '{sourceBranch}' was not found locally or on remote '{remoteName}'.");
			}

			var targetRef = await ResolveMergeTargetRefAsync(workingDirectory, targetBranch, remoteName, cancellationToken);
			if (targetRef == null)
			{
				return GitOperationResult.Failed($"Target branch '{targetBranch}' was not found locally or on remote '{remoteName}'.");
			}

			progressCallback?.Invoke(previewOnly
				? $"Preparing merge preview for {targetBranch}..."
				: $"Preparing temporary merge worktree for {targetBranch}...");

			var worktreeAddArguments = BuildTemporaryWorktreeAddArguments(tempWorktreePath, targetRef, previewOnly);
			var addWorktreeResult = await _commandExecutor.ExecuteAsync(
				worktreeAddArguments,
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 120);

			if (!addWorktreeResult.Success)
			{
				return GitOperationResult.Failed(BuildCommandError(addWorktreeResult, "Failed to create temporary merge worktree."));
			}

			worktreeAdded = true;

			progressCallback?.Invoke(previewOnly
				? $"Checking whether {sourceBranch} can merge into {targetBranch}..."
				: $"Merging {sourceBranch} into {targetBranch}...");

			var mergeArguments = previewOnly
				? $"merge --no-commit --no-ff \"{EscapeCommandArgument(sourceRef)}\""
				: $"merge --no-ff --no-edit \"{EscapeCommandArgument(sourceRef)}\"";
			var mergeResult = await _commandExecutor.ExecuteAsync(
				mergeArguments,
				tempWorktreePath,
				cancellationToken,
				timeoutSeconds: 120);

			if (!mergeResult.Success)
			{
				var mergeError = BuildCommandError(mergeResult, "Merge failed.");
				var conflictMessage = previewOnly
					? $"Merging '{sourceBranch}' into '{targetBranch}' would create conflicts. No repository changes were applied."
					: $"Merging '{sourceBranch}' into '{targetBranch}' would create conflicts. No repository changes were applied.";

				operationResult = IsMergeConflictError(mergeError)
					? GitOperationResult.Failed(conflictMessage)
					: GitOperationResult.Failed(mergeError);
			}
			else if (previewOnly)
			{
				operationResult = GitOperationResult.Succeeded(
					output: $"'{sourceBranch}' can be merged into '{targetBranch}' without conflicts.",
					branchName: sourceBranch,
					targetBranch: targetBranch,
					remoteName: remoteName);
			}
			else
			{
				var commitHash = await GetCurrentCommitHashAsync(tempWorktreePath, cancellationToken);
				if (pushAfterMerge)
				{
					progressCallback?.Invoke($"Pushing {targetBranch} to {remoteName}...");
					var pushResult = await PushAsync(tempWorktreePath, remoteName, targetBranch, cancellationToken);
					if (!pushResult.Success)
					{
						operationResult = GitOperationResult.Failed(
							$"Merge succeeded locally, but pushing '{targetBranch}' failed: {pushResult.Error}",
							commitHash: commitHash);
					}
					else
					{
						operationResult = GitOperationResult.Succeeded(
							output: $"Merged '{sourceBranch}' into '{targetBranch}' and pushed to {remoteName}.",
							commitHash: commitHash,
							branchName: targetBranch,
							remoteName: remoteName,
							targetBranch: targetBranch);
					}
				}
				else
				{
					operationResult = GitOperationResult.Succeeded(
						output: $"Merged '{sourceBranch}' into '{targetBranch}' locally. Push when you are ready.",
						commitHash: commitHash,
						branchName: targetBranch,
						remoteName: remoteName,
						targetBranch: targetBranch);
				}
			}
		}
		catch (OperationCanceledException)
		{
			operationResult = GitOperationResult.Failed("Merge operation was cancelled or timed out.");
		}
		catch (Exception ex)
		{
			operationResult = GitOperationResult.Failed($"Unexpected error merging branches: {ex.Message}");
		}
		finally
		{
			cleanupFailureMessage = await RemoveTemporaryWorktreeAsync(
				workingDirectory,
				tempWorktreePath,
				worktreeAdded,
				cancellationToken);
		}

		if (!string.IsNullOrWhiteSpace(cleanupFailureMessage))
		{
			return GitOperationResult.Failed(cleanupFailureMessage);
		}

		return operationResult ?? GitOperationResult.Failed("Merge operation did not produce a result.");
	}

	private static string TruncateDiff(string diff, int maxBytes)
	{
		var encoding = Encoding.UTF8;
		var currentBytes = 0;
		var lines = diff.Split('\n');
		var resultBuilder = new StringBuilder();

		foreach (var line in lines)
		{
			var lineBytes = encoding.GetByteCount(line) + 1;
			if (currentBytes + lineBytes > maxBytes)
			{
				resultBuilder.AppendLine();
				resultBuilder.AppendLine("... [diff truncated - exceeded 1MB limit] ...");
				break;
			}

			resultBuilder.AppendLine(line);
			currentBytes += lineBytes;
		}

		return resultBuilder.ToString();
	}

	private static GitDiffSummary ParseDiffSummary(string output)
	{
		var filesChanged = 0;
		var insertions = 0;
		var deletions = 0;

		var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		foreach (var line in lines)
		{
			if (line.Contains("changed") || line.Contains("insertion") || line.Contains("deletion"))
			{
				var parts = line.Trim().Split(',');
				foreach (var part in parts)
				{
					var trimmed = part.Trim();
					if (trimmed.Contains("file"))
					{
						var numStr = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
						if (int.TryParse(numStr, out var files))
						{
							filesChanged = files;
						}
					}
					else if (trimmed.Contains("insertion"))
					{
						var numStr = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
						if (int.TryParse(numStr, out var ins))
						{
							insertions = ins;
						}
					}
					else if (trimmed.Contains("deletion"))
					{
						var numStr = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
						if (int.TryParse(numStr, out var del))
						{
							deletions = del;
						}
					}
				}
			}
		}

		return new GitDiffSummary
		{
			FilesChanged = filesChanged,
			Insertions = insertions,
			Deletions = deletions
		};
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default)
	{
		var branches = new List<GitBranchInfo>();

		try
		{
			// Get current branch first
			var currentBranch = await GetCurrentBranchAsync(workingDirectory, cancellationToken);

			// Get all branches with commit info
			var args = includeRemote ? "branch -a --format=%(refname:short)|%(objectname:short)|%(refname)" : "branch --format=%(refname:short)|%(objectname:short)|%(refname)";
			var result = await _commandExecutor.ExecuteAsync(
				args,
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
				var seenBranches = new HashSet<string>();

				foreach (var line in lines)
				{
					var parts = line.Split('|');
					if (parts.Length < 2) continue;

					var name = parts[0].Trim();
					var commitHash = parts[1].Trim();
					var fullRef = parts.Length > 2 ? parts[2].Trim() : "";

					// Skip HEAD pointer
					if (name.Contains("HEAD")) continue;

					var isRemote = fullRef.StartsWith("refs/remotes/") || name.Contains('/');
					string shortName;
					string? remoteName = null;

					if (isRemote && name.Contains('/'))
					{
						// Extract remote name and short branch name from "origin/main"
						var slashIndex = name.IndexOf('/');
						remoteName = name[..slashIndex];
						shortName = name[(slashIndex + 1)..];
					}
					else
					{
						shortName = name;
					}

					// Skip duplicates (prefer local over remote)
					var key = isRemote ? $"remote:{name}" : $"local:{name}";
					if (!seenBranches.Add(key)) continue;

					branches.Add(new GitBranchInfo
					{
						Name = name,
						ShortName = shortName,
						IsRemote = isRemote,
						IsCurrent = !isRemote && name == currentBranch,
						RemoteName = remoteName,
						CommitHash = commitHash
					});
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get branches for {Directory}", workingDirectory);
		}

		// Sort: current branch first, then local branches, then remote branches
		return branches
			.OrderByDescending(b => b.IsCurrent)
			.ThenBy(b => b.IsRemote)
			.ThenBy(b => b.Name)
			.ToList();
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default)
	{
		try
		{
			var args = prune ? $"fetch {remoteName} --prune" : $"fetch {remoteName}";
			var result = await _commandExecutor.ExecuteAsync(
				args,
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 120);

			if (!result.Success)
			{
				var errorMessage = result.Error;

				if (errorMessage.Contains("Could not resolve host") || errorMessage.Contains("unable to access"))
				{
					return GitOperationResult.Failed("Network error: Could not reach the remote repository.");
				}

				if (errorMessage.Contains("Permission denied") || errorMessage.Contains("authentication"))
				{
					return GitOperationResult.Failed("Authentication failed. Please check your credentials.");
				}

				if (errorMessage.Contains("does not appear to be a git repository"))
				{
					return GitOperationResult.Failed($"Remote '{remoteName}' not found or is not a valid repository.");
				}

				return GitOperationResult.Failed($"Fetch failed: {errorMessage}");
			}

			return GitOperationResult.Succeeded(
				output: !string.IsNullOrWhiteSpace(result.Output) ? result.Output : "Fetch completed successfully.",
				remoteName: remoteName);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Fetch operation was cancelled or timed out");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> HardCheckoutBranchAsync(
		string workingDirectory,
		string branchName,
		string remoteName = "origin",
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			progressCallback?.Invoke($"Fetching latest from {remoteName}...");

			// First fetch to ensure we have latest refs
			var fetchResult = await FetchAsync(workingDirectory, remoteName, prune: true, cancellationToken);
			if (!fetchResult.Success)
			{
				return GitOperationResult.Failed($"Failed to fetch: {fetchResult.Error}");
			}

			progressCallback?.Invoke("Discarding local changes...");

			var preserveResult = await PreserveChangesAsync(
				workingDirectory,
				$"VibeSwarm auto-preserve before checkout to {branchName}",
				cancellationToken);

			if (!preserveResult.Success)
			{
				return GitOperationResult.Failed($"Failed to preserve local changes before checkout: {preserveResult.Error}");
			}

			// Clean untracked files and directories
			var cleanResult = await _commandExecutor.ExecuteAsync(
				"clean -fd",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			// Reset any staged/modified files
			var resetResult = await _commandExecutor.ExecuteAsync(
				"reset --hard HEAD",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			progressCallback?.Invoke($"Checking out {branchName}...");

			// Check if the branch exists locally
			var localBranchCheck = await _commandExecutor.ExecuteAsync(
				$"rev-parse --verify refs/heads/{branchName}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (localBranchCheck.Success)
			{
				// Branch exists locally, checkout and reset to remote
				var checkoutResult = await _commandExecutor.ExecuteAsync(
					$"checkout {branchName}",
					workingDirectory,
					cancellationToken,
					timeoutSeconds: 30);

				if (!checkoutResult.Success)
				{
					return GitOperationResult.Failed($"Failed to checkout branch: {checkoutResult.Error}");
				}

				progressCallback?.Invoke($"Resetting to {remoteName}/{branchName}...");

				// Check if remote tracking branch exists
				var remoteRefCheck = await _commandExecutor.ExecuteAsync(
					$"rev-parse --verify refs/remotes/{remoteName}/{branchName}",
					workingDirectory,
					cancellationToken,
					timeoutSeconds: 10);

				if (remoteRefCheck.Success)
				{
					// Reset to remote
					var hardResetResult = await _commandExecutor.ExecuteAsync(
						$"reset --hard {remoteName}/{branchName}",
						workingDirectory,
						cancellationToken,
						timeoutSeconds: 30);

					if (!hardResetResult.Success)
					{
						return GitOperationResult.Failed($"Failed to reset to remote: {hardResetResult.Error}");
					}
				}
			}
			else
			{
				// Branch doesn't exist locally, create from remote
				var remoteRefCheck = await _commandExecutor.ExecuteAsync(
					$"rev-parse --verify refs/remotes/{remoteName}/{branchName}",
					workingDirectory,
					cancellationToken,
					timeoutSeconds: 10);

				if (!remoteRefCheck.Success)
				{
					return GitOperationResult.Failed($"Branch '{branchName}' not found locally or on remote '{remoteName}'.");
				}

				// Create and checkout from remote
				var checkoutResult = await _commandExecutor.ExecuteAsync(
					$"checkout -b {branchName} {remoteName}/{branchName}",
					workingDirectory,
					cancellationToken,
					timeoutSeconds: 30);

				if (!checkoutResult.Success)
				{
					return GitOperationResult.Failed($"Failed to create and checkout branch: {checkoutResult.Error}");
				}
			}

			var commitHash = await GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
			var output = $"Successfully checked out and reset {branchName} to {remoteName}/{branchName}";
			if (preserveResult.ChangedFilesCount > 0)
			{
				output += BuildPreserveSummary(preserveResult);
			}

			return GitOperationResult.Succeeded(
				output: output,
				branchName: branchName,
				remoteName: remoteName,
				commitHash: commitHash,
				savedReference: preserveResult.SavedReference,
				changedFilesCount: preserveResult.ChangedFilesCount);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Checkout operation was cancelled or timed out");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> SyncWithOriginAsync(
		string workingDirectory,
		string remoteName = "origin",
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// Get current branch
			var currentBranch = await GetCurrentBranchAsync(workingDirectory, cancellationToken);
			if (string.IsNullOrEmpty(currentBranch))
			{
				return GitOperationResult.Failed("Could not determine current branch.");
			}

			progressCallback?.Invoke($"Fetching latest from {remoteName}...");

			// Fetch latest
			var fetchResult = await FetchAsync(workingDirectory, remoteName, prune: true, cancellationToken);
			if (!fetchResult.Success)
			{
				return GitOperationResult.Failed($"Failed to fetch: {fetchResult.Error}");
			}

			// Check if remote tracking branch exists
			var remoteRefCheck = await _commandExecutor.ExecuteAsync(
				$"rev-parse --verify refs/remotes/{remoteName}/{currentBranch}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (!remoteRefCheck.Success)
			{
				return GitOperationResult.Failed($"Remote tracking branch '{remoteName}/{currentBranch}' not found. The branch may not exist on the remote.");
			}

			progressCallback?.Invoke("Discarding local changes...");

			var preserveResult = await PreserveChangesAsync(
				workingDirectory,
				$"VibeSwarm auto-preserve before sync to {remoteName}/{currentBranch}",
				cancellationToken);

			if (!preserveResult.Success)
			{
				return GitOperationResult.Failed($"Failed to preserve local changes before sync: {preserveResult.Error}");
			}

			// Clean untracked files and directories
			await _commandExecutor.ExecuteAsync(
				"clean -fd",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			progressCallback?.Invoke($"Resetting to {remoteName}/{currentBranch}...");

			// Hard reset to remote branch
			var resetResult = await _commandExecutor.ExecuteAsync(
				$"reset --hard {remoteName}/{currentBranch}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (!resetResult.Success)
			{
				return GitOperationResult.Failed($"Failed to reset: {resetResult.Error}");
			}

			var commitHash = await GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
			var output = $"Successfully synced {currentBranch} with {remoteName}/{currentBranch}";
			if (preserveResult.ChangedFilesCount > 0)
			{
				output += BuildPreserveSummary(preserveResult);
			}

			return GitOperationResult.Succeeded(
				output: output,
				branchName: currentBranch,
				remoteName: remoteName,
				commitHash: commitHash,
				savedReference: preserveResult.SavedReference,
				changedFilesCount: preserveResult.ChangedFilesCount);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Sync operation was cancelled or timed out");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> CloneRepositoryAsync(
		string repositoryUrl,
		string targetDirectory,
		string? branch = null,
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// Validate inputs
			if (string.IsNullOrWhiteSpace(repositoryUrl))
			{
				return GitOperationResult.Failed("Repository URL cannot be empty.");
			}

			if (string.IsNullOrWhiteSpace(targetDirectory))
			{
				return GitOperationResult.Failed("Target directory cannot be empty.");
			}

			// Check if git is available
			var gitAvailable = await IsGitAvailableAsync(cancellationToken);
			if (!gitAvailable)
			{
				return GitOperationResult.Failed("Git is not available on this system.");
			}

			// Check if target directory exists and is not empty
			if (Directory.Exists(targetDirectory))
			{
				var entries = Directory.GetFileSystemEntries(targetDirectory);
				if (entries.Length > 0)
				{
					return GitOperationResult.Failed($"Target directory '{targetDirectory}' exists and is not empty.");
				}
			}
			else
			{
				// Create the parent directory if it doesn't exist
				var parentDir = Path.GetDirectoryName(targetDirectory);
				if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
				{
					Directory.CreateDirectory(parentDir);
				}
			}

			progressCallback?.Invoke($"Cloning repository from {repositoryUrl}...");

			// Build clone command
			var cloneArgs = new StringBuilder("clone");

			// Add branch if specified
			if (!string.IsNullOrWhiteSpace(branch))
			{
				cloneArgs.Append($" --branch {branch}");
			}

			// Add progress flag for better feedback
			cloneArgs.Append(" --progress");

			// Add the repository URL and target directory
			cloneArgs.Append($" \"{repositoryUrl}\" \"{targetDirectory}\"");

			// Use the parent directory as working directory since target doesn't exist yet
			var workingDir = Path.GetDirectoryName(targetDirectory) ?? Directory.GetCurrentDirectory();

			// Clone can take longer, increase timeout to 5 minutes
			var result = await _commandExecutor.ExecuteAsync(
				cloneArgs.ToString(),
				workingDir,
				cancellationToken,
				timeoutSeconds: 300);

			if (!result.Success)
			{
				// Clean up partially cloned directory if it exists
				if (Directory.Exists(targetDirectory))
				{
					try
					{
						Directory.Delete(targetDirectory, recursive: true);
					}
					catch
					{
						// Ignore cleanup errors
					}
				}

				var errorMessage = !string.IsNullOrWhiteSpace(result.Error)
					? result.Error.Trim()
					: "Clone operation failed with no error message.";

				return GitOperationResult.Failed($"Failed to clone repository: {errorMessage}");
			}

			progressCallback?.Invoke("Clone completed. Getting repository info...");

			// Get the commit hash and branch info from the cloned repository
			var commitHash = await GetCurrentCommitHashAsync(targetDirectory, cancellationToken);
			var currentBranch = await GetCurrentBranchAsync(targetDirectory, cancellationToken);

			return GitOperationResult.Succeeded(
				output: $"Successfully cloned repository to {targetDirectory}",
				branchName: currentBranch,
				commitHash: commitHash);
		}
		catch (OperationCanceledException)
		{
			// Clean up partially cloned directory
			if (Directory.Exists(targetDirectory))
			{
				try
				{
					Directory.Delete(targetDirectory, recursive: true);
				}
				catch
				{
					// Ignore cleanup errors
				}
			}

			return GitOperationResult.Failed("Clone operation was cancelled or timed out.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error during clone: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true)
	{
		if (string.IsNullOrWhiteSpace(ownerAndRepo))
		{
			throw new ArgumentException("Owner and repo cannot be empty.", nameof(ownerAndRepo));
		}

		// Trim whitespace and remove any leading/trailing slashes
		var normalized = ownerAndRepo.Trim().Trim('/');

		// Validate format: should be "owner/repo"
		var parts = normalized.Split('/');
		if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
		{
			throw new ArgumentException("Invalid format. Expected 'owner/repo' format (e.g., 'microsoft/vscode').", nameof(ownerAndRepo));
		}

		// Return SSH or HTTPS URL based on preference
		// SSH is preferred for private repos as it uses SSH keys for authentication
		if (useSsh)
		{
			return $"git@github.com:{parts[0]}/{parts[1]}.git";
		}

		return $"https://github.com/{parts[0]}/{parts[1]}.git";
	}

	/// <inheritdoc />
	public string? ExtractGitHubRepository(string? remoteUrl)
	{
		if (string.IsNullOrWhiteSpace(remoteUrl))
		{
			return null;
		}

		var url = remoteUrl.Trim();

		// Handle SSH format: git@github.com:owner/repo.git
		if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
		{
			var path = url.Substring("git@github.com:".Length);
			// Remove .git suffix if present
			if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			{
				path = path.Substring(0, path.Length - 4);
			}
			return path;
		}

		// Handle HTTPS format: https://github.com/owner/repo.git or https://github.com/owner/repo
		if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
		{
			var path = url.Substring("https://github.com/".Length);
			// Remove .git suffix if present
			if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			{
				path = path.Substring(0, path.Length - 4);
			}
			// Remove trailing slash if present
			path = path.TrimEnd('/');
			return path;
		}

		// Handle HTTP format (less common): http://github.com/owner/repo.git
		if (url.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
		{
			var path = url.Substring("http://github.com/".Length);
			// Remove .git suffix if present
			if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			{
				path = path.Substring(0, path.Length - 4);
			}
			// Remove trailing slash if present
			path = path.TrimEnd('/');
			return path;
		}

		// Not a recognized GitHub URL format
		return null;
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> CreateBranchAsync(
		string workingDirectory,
		string branchName,
		bool switchToBranch = true,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// Validate inputs
			if (string.IsNullOrWhiteSpace(workingDirectory))
			{
				return GitOperationResult.Failed("Working directory cannot be empty.");
			}

			if (string.IsNullOrWhiteSpace(branchName))
			{
				return GitOperationResult.Failed("Branch name cannot be empty.");
			}

			// Validate branch name format (basic check)
			if (branchName.Contains(" ") || branchName.Contains("..") || branchName.StartsWith("-"))
			{
				return GitOperationResult.Failed("Invalid branch name. Branch names cannot contain spaces, '..' sequences, or start with '-'.");
			}

			// Check if git is available
			var gitAvailable = await IsGitAvailableAsync(cancellationToken);
			if (!gitAvailable)
			{
				return GitOperationResult.Failed("Git is not available on this system.");
			}

			// Check if directory is a git repository
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return GitOperationResult.Failed("The specified directory is not a git repository.");
			}

			// Check if branch already exists
			var branchCheckResult = await _commandExecutor.ExecuteAsync(
				$"rev-parse --verify refs/heads/{branchName}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (branchCheckResult.Success)
			{
				return GitOperationResult.Failed($"Branch '{branchName}' already exists.");
			}

			// Create the branch (optionally switching to it)
			var command = switchToBranch
				? $"checkout -b {branchName}"
				: $"branch {branchName}";

			var createResult = await _commandExecutor.ExecuteAsync(
				command,
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (!createResult.Success)
			{
				var errorMessage = !string.IsNullOrWhiteSpace(createResult.Error)
					? createResult.Error.Trim()
					: "Failed to create branch.";

				return GitOperationResult.Failed($"Failed to create branch: {errorMessage}");
			}

			// Get the commit hash
			var commitHash = await GetCurrentCommitHashAsync(workingDirectory, cancellationToken);

			return GitOperationResult.Succeeded(
				output: switchToBranch
					? $"Created and switched to new branch '{branchName}'"
					: $"Created new branch '{branchName}'",
				branchName: branchName,
				commitHash: commitHash);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Create branch operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error creating branch: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> DiscardAllChangesAsync(
		string workingDirectory,
		bool includeUntracked = true,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// Check if git is available
			var gitAvailable = await IsGitAvailableAsync(cancellationToken);
			if (!gitAvailable)
			{
				return GitOperationResult.Failed("Git is not available on this system.");
			}

			// Check if directory is a git repository
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return GitOperationResult.Failed("The specified directory is not a git repository.");
			}

			// Reset all tracked changes (staged and unstaged)
			var resetResult = await _commandExecutor.ExecuteAsync(
				"reset --hard HEAD",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (!resetResult.Success)
			{
				var errorMessage = !string.IsNullOrWhiteSpace(resetResult.Error)
					? resetResult.Error.Trim()
					: "Failed to reset changes.";

				return GitOperationResult.Failed($"Failed to reset changes: {errorMessage}");
			}

			// Optionally clean untracked files
			if (includeUntracked)
			{
				var cleanResult = await _commandExecutor.ExecuteAsync(
					"clean -fd",
					workingDirectory,
					cancellationToken,
					timeoutSeconds: 30);

				if (!cleanResult.Success)
				{
					// Reset succeeded but clean failed - still report partial success
					return GitOperationResult.Succeeded(
						output: "Tracked changes discarded, but failed to remove untracked files.");
				}
			}

			return GitOperationResult.Succeeded(
				output: includeUntracked
					? "All changes discarded (including untracked files)."
					: "All tracked changes discarded.");
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Discard changes operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error discarding changes: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> PreserveChangesAsync(
		string workingDirectory,
		string message,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return GitOperationResult.Failed("The specified directory is not a git repository.");
			}

			var workingTreeStatus = await GetWorkingTreeStatusAsync(workingDirectory, cancellationToken);
			if (!workingTreeStatus.HasUncommittedChanges)
			{
				return GitOperationResult.Succeeded(
					output: "No local changes to preserve.",
					changedFilesCount: 0);
			}

			var escapedMessage = EscapeCommandArgument(message);
			var stashResult = await _commandExecutor.ExecuteAsync(
				$"stash push --include-untracked --message \"{escapedMessage}\"",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (!stashResult.Success)
			{
				var errorMessage = BuildCommandError(stashResult, "Failed to preserve local changes.");
				return GitOperationResult.Failed(errorMessage);
			}

			if (stashResult.Output.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase))
			{
				return GitOperationResult.Succeeded(
					output: "No local changes to preserve.",
					changedFilesCount: 0);
			}

			var savedReferenceResult = await _commandExecutor.ExecuteAsync(
				"rev-parse --verify stash@{0}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			var savedReference = savedReferenceResult.Success && !string.IsNullOrWhiteSpace(savedReferenceResult.Output)
				? savedReferenceResult.Output.Trim()
				: "stash@{0}";

			return GitOperationResult.Succeeded(
				output: $"Preserved {workingTreeStatus.ChangedFilesCount} changed file(s) in {savedReference}.",
				savedReference: savedReference,
				changedFilesCount: workingTreeStatus.ChangedFilesCount);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Preserve changes operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error preserving changes: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<string>> GetCommitLogAsync(
		string workingDirectory,
		string fromCommit,
		string? toCommit = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return Array.Empty<string>();
			}

			var targetCommit = string.IsNullOrEmpty(toCommit) ? "HEAD" : toCommit;

			// Use --oneline format which gives us: <short-hash> <subject>
			// We want the subject line only, so we'll strip the hash
			var result = await _commandExecutor.ExecuteAsync(
				$"log {fromCommit}..{targetCommit} --oneline --no-decorate",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return result.Output
					.Split('\n', StringSplitOptions.RemoveEmptyEntries)
					.Select(line =>
					{
						// Strip the hash prefix (format: "abc1234 Subject line")
						var spaceIndex = line.IndexOf(' ');
						return spaceIndex > 0 ? line[(spaceIndex + 1)..].Trim() : line.Trim();
					})
					.Where(msg => !string.IsNullOrEmpty(msg))
					.ToList();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get commit log for {Directory} (from: {FromCommit})", workingDirectory, fromCommit);
		}

		return Array.Empty<string>();
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> InitializeRepositoryAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (!Directory.Exists(workingDirectory))
			{
				return GitOperationResult.Failed($"Directory does not exist: {workingDirectory}");
			}

			// Check if already a git repository
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (isRepo)
			{
				return GitOperationResult.Failed("Directory is already a git repository.");
			}

			var result = await _commandExecutor.ExecuteAsync(
				"init",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: DefaultTimeoutSeconds);

			if (result.Success)
			{
				return GitOperationResult.Succeeded(output: result.Output?.Trim());
			}

			return GitOperationResult.Failed(result.Error ?? "Failed to initialize git repository.");
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Git init operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error initializing repository: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				"--version",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 5);

			return result.Success && result.Output.Contains("gh version");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check GitHub CLI availability");
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				"auth status",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 10);

			// gh auth status returns exit code 0 if logged in
			return result.Success;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check GitHub CLI authentication");
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> CreateGitHubRepositoryAsync(
		string workingDirectory,
		string repositoryName,
		string? description = null,
		bool isPrivate = false,
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default,
		string? gitignoreTemplate = null,
		string? licenseTemplate = null,
		bool initializeReadme = false)
	{
		try
		{
			// Check if gh CLI is available
			var ghAvailable = await IsGitHubCliAvailableAsync(cancellationToken);
			if (!ghAvailable)
			{
				return GitOperationResult.Failed("GitHub CLI (gh) is not installed. Please install it from https://cli.github.com/");
			}

			// Check authentication
			var ghAuthenticated = await IsGitHubCliAuthenticatedAsync(cancellationToken);
			if (!ghAuthenticated)
			{
				return GitOperationResult.Failed("Not authenticated with GitHub CLI. Please run 'gh auth login' to authenticate.");
			}

			var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
			if (!Directory.Exists(fullWorkingDirectory) || !Directory.EnumerateFileSystemEntries(fullWorkingDirectory).Any())
			{
				return await CreateAndCloneGitHubRepositoryAsync(
					fullWorkingDirectory,
					repositoryName,
					description,
					isPrivate,
					progressCallback,
					cancellationToken,
					gitignoreTemplate,
					licenseTemplate,
					initializeReadme);
			}

			progressCallback?.Invoke("Checking git repository status...");

			// Check if it's a git repository, if not initialize it
			var isRepo = await IsGitRepositoryAsync(fullWorkingDirectory, cancellationToken);
			if (!isRepo)
			{
				progressCallback?.Invoke("Initializing git repository...");
				var initResult = await InitializeRepositoryAsync(fullWorkingDirectory, cancellationToken);
				if (!initResult.Success)
				{
					return GitOperationResult.Failed($"Failed to initialize git repository: {initResult.Error}");
				}
			}

			// Check if remote already exists
			var existingRemote = await GetRemoteUrlAsync(fullWorkingDirectory, "origin", cancellationToken);
			if (!string.IsNullOrEmpty(existingRemote))
			{
				return GitOperationResult.Failed($"Remote 'origin' already exists: {existingRemote}");
			}

			progressCallback?.Invoke("Creating GitHub repository...");

			// Build the gh repo create command
			var visibility = isPrivate ? "--private" : "--public";
			var descArg = !string.IsNullOrWhiteSpace(description)
				? $"--description \"{description.Replace("\"", "\\\"")}\""
				: "";

			var gitignoreArg = !string.IsNullOrEmpty(gitignoreTemplate)
				? $"--gitignore \"{gitignoreTemplate}\""
				: "";
			var licenseArg = !string.IsNullOrEmpty(licenseTemplate)
				? $"--license \"{licenseTemplate}\""
				: "";
			var readmeArg = initializeReadme ? "--add-readme" : "";

			// Use gh repo create with --source flag to link to existing directory
			var ghArgs = $"repo create \"{repositoryName}\" {visibility} {descArg} {gitignoreArg} {licenseArg} {readmeArg} --source \"{workingDirectory}\" --remote origin --push".Trim();

			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				ghArgs,
				fullWorkingDirectory,
				cancellationToken,
				timeoutSeconds: 120);

			if (result.Success)
			{
				// Try to get the new remote URL
				var newRemoteUrl = await GetRemoteUrlAsync(fullWorkingDirectory, "origin", cancellationToken);

				progressCallback?.Invoke("Repository created successfully!");

				return GitOperationResult.Succeeded(
					output: result.Output?.Trim(),
					remoteName: "origin");
			}

			// Parse error message
			var errorMessage = result.Error ?? result.Output ?? "Failed to create GitHub repository.";

			// Check for common errors
			if (errorMessage.Contains("already exists"))
			{
				return GitOperationResult.Failed($"Repository '{repositoryName}' already exists on GitHub. Please choose a different name.");
			}

			return GitOperationResult.Failed(errorMessage.Trim());
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Create GitHub repository operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error creating GitHub repository: {ex.Message}");
		}
	}

	private async Task<GitOperationResult> CreateAndCloneGitHubRepositoryAsync(
		string workingDirectory,
		string repositoryName,
		string? description,
		bool isPrivate,
		Action<string>? progressCallback,
		CancellationToken cancellationToken,
		string? gitignoreTemplate,
		string? licenseTemplate,
		bool initializeReadme)
	{
		var parentDirectory = Path.GetDirectoryName(workingDirectory);
		if (string.IsNullOrWhiteSpace(parentDirectory))
		{
			return GitOperationResult.Failed($"Unable to determine a parent directory for '{workingDirectory}'.");
		}

		Directory.CreateDirectory(parentDirectory);

		var targetDirectoryExists = Directory.Exists(workingDirectory);
		if (targetDirectoryExists && Directory.EnumerateFileSystemEntries(workingDirectory).Any())
		{
			return GitOperationResult.Failed($"Target directory must be empty before creating and cloning a new repository: {workingDirectory}");
		}

		if (targetDirectoryExists)
		{
			Directory.Delete(workingDirectory);
		}

		var cloneDirectoryName = GetRepositoryDirectoryName(repositoryName);
		var cloneDirectory = Path.Combine(parentDirectory, cloneDirectoryName);
		if (!PathsEqual(cloneDirectory, workingDirectory) && Directory.Exists(cloneDirectory))
		{
			return GitOperationResult.Failed($"A directory already exists for the new repository clone: {cloneDirectory}");
		}

		progressCallback?.Invoke("Creating GitHub repository and cloning workspace...");

		var visibility = isPrivate ? "--private" : "--public";
		var descArg = !string.IsNullOrWhiteSpace(description)
			? $"--description \"{description.Replace("\"", "\\\"")}\""
			: "";
		var gitignoreArg = !string.IsNullOrEmpty(gitignoreTemplate)
			? $"--gitignore \"{gitignoreTemplate}\""
			: "";
		var licenseArg = !string.IsNullOrEmpty(licenseTemplate)
			? $"--license \"{licenseTemplate}\""
			: "";
		var readmeArg = initializeReadme ? "--add-readme" : "";
		var ghArgs = $"repo create \"{repositoryName}\" {visibility} {descArg} {gitignoreArg} {licenseArg} {readmeArg} --clone".Trim();

		var result = await _commandExecutor.ExecuteRawAsync(
			"gh",
			ghArgs,
			parentDirectory,
			cancellationToken,
			timeoutSeconds: 120);

		if (!result.Success)
		{
			if (targetDirectoryExists)
			{
				Directory.CreateDirectory(workingDirectory);
			}

			return ParseCreateRepositoryFailure(result, repositoryName);
		}

		if (!Directory.Exists(cloneDirectory))
		{
			return GitOperationResult.Failed($"GitHub repository was created, but the local clone directory was not found: {cloneDirectory}");
		}

		if (!PathsEqual(cloneDirectory, workingDirectory))
		{
			Directory.Move(cloneDirectory, workingDirectory);
		}

		progressCallback?.Invoke("Repository created successfully!");
		return GitOperationResult.Succeeded(output: result.Output?.Trim(), remoteName: "origin");
	}

	private static GitOperationResult ParseCreateRepositoryFailure(GitCommandResult result, string repositoryName)
	{
		var errorMessage = result.Error ?? result.Output ?? "Failed to create GitHub repository.";
		if (errorMessage.Contains("already exists", StringComparison.OrdinalIgnoreCase))
		{
			return GitOperationResult.Failed($"Repository '{repositoryName}' already exists on GitHub. Please choose a different name.");
		}

		return GitOperationResult.Failed(errorMessage.Trim());
	}

	private static string GetRepositoryDirectoryName(string repositoryName)
	{
		var segments = repositoryName
			.Replace('\\', '/')
			.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		return segments.LastOrDefault() ?? repositoryName;
	}

	private static bool PathsEqual(string left, string right)
	{
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
	}

	/// <inheritdoc />
	public async Task<GitOperationResult> AddRemoteAsync(
		string workingDirectory,
		string remoteName,
		string remoteUrl,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return GitOperationResult.Failed("The specified directory is not a git repository.");
			}

			// Check if remote already exists
			var existingUrl = await GetRemoteUrlAsync(workingDirectory, remoteName, cancellationToken);
			if (!string.IsNullOrEmpty(existingUrl))
			{
				return GitOperationResult.Failed($"Remote '{remoteName}' already exists with URL: {existingUrl}");
			}

			var result = await _commandExecutor.ExecuteAsync(
				$"remote add {remoteName} \"{remoteUrl}\"",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: DefaultTimeoutSeconds);

			if (result.Success)
			{
				return GitOperationResult.Succeeded(
					output: $"Remote '{remoteName}' added successfully.",
					remoteName: remoteName);
			}

			return GitOperationResult.Failed(result.Error ?? "Failed to add remote.");
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Add remote operation was cancelled.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error adding remote: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		var remotes = new Dictionary<string, string>();

		try
		{
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return remotes;
			}

			var result = await _commandExecutor.ExecuteAsync(
				"remote -v",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: DefaultTimeoutSeconds);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				// Parse output: "origin  git@github.com:owner/repo.git (fetch)"
				foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
				{
					var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length >= 2)
					{
						var name = parts[0];
						var url = parts[1];

						// Only add if not already present (we get both fetch and push lines)
						if (!remotes.ContainsKey(name))
						{
							remotes[name] = url;
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to get remotes for {Directory}", workingDirectory);
		}

		return remotes;
	}

	/// <inheritdoc />
	/// <inheritdoc />
	public async Task<GitOperationResult> CloneWithGitHubCliAsync(
		string ownerRepo,
		string targetDirectory,
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(ownerRepo))
			{
				return GitOperationResult.Failed("Owner/repo cannot be empty.");
			}

			if (string.IsNullOrWhiteSpace(targetDirectory))
			{
				return GitOperationResult.Failed("Target directory cannot be empty.");
			}

			var ghAvailable = await IsGitHubCliAvailableAsync(cancellationToken);
			if (!ghAvailable)
			{
				return GitOperationResult.Failed("GitHub CLI (gh) is not installed.");
			}

			var ghAuthenticated = await IsGitHubCliAuthenticatedAsync(cancellationToken);
			if (!ghAuthenticated)
			{
				return GitOperationResult.Failed("Not authenticated with GitHub CLI. Please run 'gh auth login'.");
			}

			// Check if target directory exists and is not empty
			if (Directory.Exists(targetDirectory))
			{
				var entries = Directory.GetFileSystemEntries(targetDirectory);
				if (entries.Length > 0)
				{
					return GitOperationResult.Failed($"Target directory '{targetDirectory}' exists and is not empty.");
				}
			}
			else
			{
				var parentDir = Path.GetDirectoryName(targetDirectory);
				if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
				{
					Directory.CreateDirectory(parentDir);
				}
			}

			progressCallback?.Invoke($"Cloning {ownerRepo} using GitHub CLI...");

			var workingDir = Path.GetDirectoryName(targetDirectory) ?? Directory.GetCurrentDirectory();

			var result = await _commandExecutor.ExecuteRawAsync(
				"gh",
				$"repo clone {ownerRepo} \"{targetDirectory}\"",
				workingDir,
				cancellationToken,
				timeoutSeconds: 300);

			if (!result.Success)
			{
				// Clean up partially cloned directory
				if (Directory.Exists(targetDirectory))
				{
					try { Directory.Delete(targetDirectory, true); } catch { }
				}
				return GitOperationResult.Failed($"gh clone failed: {result.Error}");
			}

			progressCallback?.Invoke("Clone complete.");

			return GitOperationResult.Succeeded(output: $"Successfully cloned {ownerRepo}");
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Clone operation was cancelled or timed out.");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	public async Task<GitOperationResult> PruneRemoteBranchesAsync(
		string workingDirectory,
		string remoteName = "origin",
		CancellationToken cancellationToken = default)
	{
		try
		{
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return GitOperationResult.Failed("The specified directory is not a git repository.");
			}

			var result = await _commandExecutor.ExecuteAsync(
				$"remote prune {remoteName}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 60);

			if (!result.Success)
			{
				var errorMessage = result.Error;

				if (errorMessage.Contains("does not appear to be a git repository"))
				{
					return GitOperationResult.Failed($"Remote '{remoteName}' not found or is not a valid repository.");
				}

				return GitOperationResult.Failed($"Prune failed: {errorMessage}");
			}

			// Count pruned branches from output (lines containing "[pruned]")
			var output = result.Output + result.Error; // Git may write to stderr
			var prunedCount = output
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Count(line => line.Contains("[pruned]", StringComparison.OrdinalIgnoreCase));

			var message = prunedCount > 0
				? $"Pruned {prunedCount} stale remote-tracking branch(es) from {remoteName}."
				: $"No stale branches to prune from {remoteName}.";

			return GitOperationResult.Succeeded(
				output: message,
				remoteName: remoteName);
		}
		catch (OperationCanceledException)
		{
			return GitOperationResult.Failed("Prune operation was cancelled or timed out");
		}
		catch (Exception ex)
		{
			return GitOperationResult.Failed($"Unexpected error: {ex.Message}");
		}
	}

	private async Task<string?> ResolveMergeSourceRefAsync(
		string workingDirectory,
		string sourceBranch,
		string remoteName,
		CancellationToken cancellationToken)
	{
		var localRef = await _commandExecutor.ExecuteAsync(
			$"rev-parse --verify refs/heads/{sourceBranch}",
			workingDirectory,
			cancellationToken,
			timeoutSeconds: 10);
		if (localRef.Success)
		{
			return sourceBranch;
		}

		var remoteRef = await _commandExecutor.ExecuteAsync(
			$"rev-parse --verify refs/remotes/{remoteName}/{sourceBranch}",
			workingDirectory,
			cancellationToken,
			timeoutSeconds: 10);
		return remoteRef.Success ? $"{remoteName}/{sourceBranch}" : null;
	}

	private async Task<MergeTargetResolution?> ResolveMergeTargetRefAsync(
		string workingDirectory,
		string targetBranch,
		string remoteName,
		CancellationToken cancellationToken)
	{
		var remoteRef = await _commandExecutor.ExecuteAsync(
			$"rev-parse --verify refs/remotes/{remoteName}/{targetBranch}",
			workingDirectory,
			cancellationToken,
			timeoutSeconds: 10);
		if (remoteRef.Success)
		{
			return new MergeTargetResolution($"{remoteName}/{targetBranch}", targetBranch, true);
		}

		var localRef = await _commandExecutor.ExecuteAsync(
			$"rev-parse --verify refs/heads/{targetBranch}",
			workingDirectory,
			cancellationToken,
			timeoutSeconds: 10);
		if (localRef.Success)
		{
			return new MergeTargetResolution(targetBranch, targetBranch, false);
		}

		return null;
	}

	private async Task<GitOperationResult?> ValidateMergeRequestAsync(
		string workingDirectory,
		string sourceBranch,
		string targetBranch,
		string remoteName,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(sourceBranch))
		{
			return GitOperationResult.Failed("Source branch cannot be empty.");
		}

		if (string.IsNullOrWhiteSpace(targetBranch))
		{
			return GitOperationResult.Failed("Target branch cannot be empty.");
		}

		if (string.Equals(sourceBranch, targetBranch, StringComparison.Ordinal))
		{
			return GitOperationResult.Failed("Source and target branches must be different.");
		}

		if (string.IsNullOrWhiteSpace(remoteName))
		{
			return GitOperationResult.Failed("Remote name cannot be empty.");
		}

		var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
		if (!isRepo)
		{
			return GitOperationResult.Failed("The specified directory is not a git repository.");
		}

		return null;
	}

	private string BuildTemporaryWorktreeAddArguments(
		string tempWorktreePath,
		MergeTargetResolution targetRef,
		bool previewOnly)
	{
		var escapedPath = EscapeCommandArgument(tempWorktreePath);
		var escapedTargetBranch = EscapeCommandArgument(targetRef.LocalBranchName);
		var escapedTargetRef = EscapeCommandArgument(targetRef.Reference);

		if (previewOnly)
		{
			return $"worktree add --force --detach \"{escapedPath}\" \"{escapedTargetRef}\"";
		}

		return targetRef.CreateOrResetLocalBranch
			? $"worktree add --force -B \"{escapedTargetBranch}\" \"{escapedPath}\" \"{escapedTargetRef}\""
			: $"worktree add --force \"{escapedPath}\" \"{escapedTargetRef}\"";
	}

	private async Task<string?> RemoveTemporaryWorktreeAsync(
		string workingDirectory,
		string tempWorktreePath,
		bool worktreeAdded,
		CancellationToken cancellationToken)
	{
		try
		{
			if (worktreeAdded)
			{
				var removeResult = await _commandExecutor.ExecuteAsync(
					$"worktree remove --force \"{EscapeCommandArgument(tempWorktreePath)}\"",
					workingDirectory,
					cancellationToken,
					timeoutSeconds: 60);

				if (!removeResult.Success)
				{
					var error = BuildCommandError(removeResult, "Failed to remove temporary merge worktree.");
					_logger.LogWarning("Temporary merge worktree cleanup failed for {Worktree}: {Error}", tempWorktreePath, error);
					return $"Merge cleanup failed for temporary worktree '{tempWorktreePath}': {error}";
				}

				await _commandExecutor.ExecuteAsync(
					"worktree prune",
					workingDirectory,
					cancellationToken,
					timeoutSeconds: 30);
			}
			else if (Directory.Exists(tempWorktreePath))
			{
				Directory.Delete(tempWorktreePath, recursive: true);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to clean up temporary merge worktree {Worktree}", tempWorktreePath);
			return $"Merge cleanup failed for temporary worktree '{tempWorktreePath}': {ex.Message}";
		}

		return null;
	}

	private static bool IsMergeConflictError(string error)
		=> error.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
			|| error.Contains("Automatic merge failed", StringComparison.OrdinalIgnoreCase);

	private static string BuildCommandError(GitCommandResult result, string fallbackMessage)
	{
		var parts = new[] { result.Error, result.Output }
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.ToArray();
		return parts.Length > 0 ? string.Join(Environment.NewLine, parts) : fallbackMessage;
	}

	private static string EscapeCommandArgument(string value)
		=> value.Replace("\\", "\\\\").Replace("\"", "\\\"");

	private static string BuildPreserveSummary(GitOperationResult result)
	{
		var savedReference = result.SavedReference ?? "stash@{0}";
		var changedFilesCount = result.ChangedFilesCount ?? 0;
		return $" Preserved {changedFilesCount} changed file(s) in {savedReference} before continuing.";
	}

	private sealed record MergeTargetResolution(string Reference, string LocalBranchName, bool CreateOrResetLocalBranch);

	private static List<string> ParseWorkingTreeStatus(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return [];
		}

		var changedFiles = new List<string>();
		foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var line = rawLine.TrimEnd('\r');
			if (line.Length < 4)
			{
				continue;
			}

			var path = line[3..].Trim();
			var renameSeparatorIndex = path.LastIndexOf(" -> ", StringComparison.Ordinal);
			if (renameSeparatorIndex >= 0)
			{
				path = path[(renameSeparatorIndex + 4)..].Trim();
			}

			if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
			{
				path = path[1..^1];
			}

			if (!string.IsNullOrWhiteSpace(path))
			{
				changedFiles.Add(path);
			}
		}

		return changedFiles
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	private static string? ExtractPullRequestUrl(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return null;
		}

		foreach (var token in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
		{
			if (Uri.TryCreate(token.Trim(), UriKind.Absolute, out var uri) &&
				uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase) &&
				uri.AbsolutePath.Contains("/pull/", StringComparison.Ordinal))
			{
				return uri.ToString();
			}
		}

		return null;
	}

	private static int? ExtractPullRequestNumber(string? pullRequestUrl)
	{
		if (string.IsNullOrWhiteSpace(pullRequestUrl))
		{
			return null;
		}

		if (!Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out var uri))
		{
			return null;
		}

		var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var pullSegmentIndex = Array.IndexOf(segments, "pull");
		if (pullSegmentIndex < 0 || pullSegmentIndex + 1 >= segments.Length)
		{
			return null;
		}

		return int.TryParse(segments[pullSegmentIndex + 1], out var number) ? number : null;
	}
}
