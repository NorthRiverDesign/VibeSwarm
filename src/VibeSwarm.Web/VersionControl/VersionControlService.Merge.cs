using VibeSwarm.Shared.VersionControl.Models;
namespace VibeSwarm.Shared.VersionControl;

public sealed partial class VersionControlService
{
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

			var ghArgs = new System.Text.StringBuilder("pr create");
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
}
