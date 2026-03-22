using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared;

namespace VibeSwarm.Shared.VersionControl;

public sealed partial class VersionControlService
{
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
				$"{AppConstants.AppName} auto-preserve before checkout to {branchName}",
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
				$"{AppConstants.AppName} auto-preserve before sync to {remoteName}/{currentBranch}",
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
}
