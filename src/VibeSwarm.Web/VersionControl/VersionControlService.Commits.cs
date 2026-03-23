using VibeSwarm.Shared;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

public sealed partial class VersionControlService
{
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

			// Strip leading newlines/whitespace that agents sometimes prepend (e.g. "\nAdd a feature")
			commitMessage = commitMessage.TrimStart('\n', '\r');

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
}
