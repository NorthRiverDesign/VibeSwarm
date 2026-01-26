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
}
