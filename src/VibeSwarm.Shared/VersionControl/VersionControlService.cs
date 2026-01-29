using System.Text;
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

	public VersionControlService(IGitCommandExecutor commandExecutor)
	{
		_commandExecutor = commandExecutor;
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
		catch
		{
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
		catch
		{
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
		catch
		{
			// Git not available or not a git repository
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
		catch
		{
			// Git not available or error
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
		catch
		{
			// Git not available or error
		}

		return null;
	}

	/// <inheritdoc />
	public async Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _commandExecutor.ExecuteAsync(
				"status --porcelain",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			return result.Success && !string.IsNullOrWhiteSpace(result.Output);
		}
		catch
		{
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default)
	{
		try
		{
			var diffTarget = string.IsNullOrEmpty(baseCommit) ? "HEAD" : baseCommit;
			var result = await _commandExecutor.ExecuteAsync(
				$"diff {diffTarget} --name-only",
				workingDirectory,
				cancellationToken);

			if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
			{
				return result.Output
					.Split('\n', StringSplitOptions.RemoveEmptyEntries)
					.Select(f => f.Trim())
					.Where(f => !string.IsNullOrEmpty(f))
					.ToList();
			}
		}
		catch
		{
			// Git not available or error
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

			var result = await _commandExecutor.ExecuteAsync(
				$"diff {diffTarget} --stat --patch --find-renames --find-copies",
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

			// If diff against commit fails (e.g., no commits yet), try diff against empty tree
			if (!result.Success)
			{
				var emptyTreeResult = await _commandExecutor.ExecuteAsync(
					"diff --cached --stat --patch",
					workingDirectory,
					cancellationToken);

				if (emptyTreeResult.Success && !string.IsNullOrWhiteSpace(emptyTreeResult.Output))
				{
					return emptyTreeResult.Output;
				}
			}
		}
		catch
		{
			// Git not available or error running command
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
		catch
		{
			// Git not available or error running command
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
		catch
		{
			// Git not available or error
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
		catch
		{
			// Git not available or error
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

			return GitOperationResult.Succeeded(
				output: $"Successfully checked out and reset {branchName} to {remoteName}/{branchName}",
				branchName: branchName,
				remoteName: remoteName,
				commitHash: commitHash);
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

			return GitOperationResult.Succeeded(
				output: $"Successfully synced {currentBranch} with {remoteName}/{currentBranch}",
				branchName: currentBranch,
				remoteName: remoteName,
				commitHash: commitHash);
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
}
