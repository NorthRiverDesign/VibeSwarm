using System.Diagnostics;
using System.Text;

namespace VibeSwarm.Shared.Utilities;

/// <summary>
/// Helper class for interacting with git repositories.
/// Assumes git is installed and available in the system PATH.
/// </summary>
public static class GitHelper
{
	private const int DefaultTimeoutSeconds = 30;
	private const int MaxDiffSizeBytes = 1024 * 1024; // 1 MB max diff size

	/// <summary>
	/// Gets the current HEAD commit hash for a repository.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The commit hash, or null if not a git repository or git not available</returns>
	public static async Task<string?> GetCurrentCommitHashAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await RunGitCommandAsync(
				"rev-parse HEAD",
				workingDirectory,
				cancellationToken);

			if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
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

	/// <summary>
	/// Gets the diff of all changes in the working directory (staged and unstaged).
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="baseCommit">Optional base commit to compare against (defaults to HEAD)</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The diff output, or null if not available</returns>
	public static async Task<string?> GetWorkingDirectoryDiffAsync(
		string workingDirectory,
		string? baseCommit = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// First check if this is a git repository
			var isRepo = await IsGitRepositoryAsync(workingDirectory, cancellationToken);
			if (!isRepo)
			{
				return null;
			}

			// Get diff including both staged and unstaged changes
			// Using HEAD shows all uncommitted changes
			var diffTarget = string.IsNullOrEmpty(baseCommit) ? "HEAD" : baseCommit;

			// Get diff with some context and detect renames
			var result = await RunGitCommandAsync(
				$"diff {diffTarget} --stat --patch --find-renames --find-copies",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 60);

			if (result.ExitCode == 0)
			{
				var diff = result.Output;

				// Truncate if too large
				if (!string.IsNullOrEmpty(diff) && Encoding.UTF8.GetByteCount(diff) > MaxDiffSizeBytes)
				{
					diff = TruncateDiff(diff, MaxDiffSizeBytes);
				}

				return string.IsNullOrWhiteSpace(diff) ? null : diff;
			}

			// If diff against commit fails (e.g., no commits yet), try diff against empty tree
			if (result.ExitCode != 0)
			{
				var emptyTreeResult = await RunGitCommandAsync(
					"diff --cached --stat --patch",
					workingDirectory,
					cancellationToken);

				if (emptyTreeResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(emptyTreeResult.Output))
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

	/// <summary>
	/// Gets the diff of changes between two commits.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="fromCommit">The starting commit</param>
	/// <param name="toCommit">The ending commit (defaults to HEAD)</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The diff output, or null if not available</returns>
	public static async Task<string?> GetCommitRangeDiffAsync(
		string workingDirectory,
		string fromCommit,
		string? toCommit = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var targetCommit = string.IsNullOrEmpty(toCommit) ? "HEAD" : toCommit;
			var result = await RunGitCommandAsync(
				$"diff {fromCommit}..{targetCommit} --stat --patch --find-renames --find-copies",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 60);

			if (result.ExitCode == 0)
			{
				var diff = result.Output;

				// Truncate if too large
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

	/// <summary>
	/// Gets a summary of changed files without full diff content.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="baseCommit">Optional base commit to compare against</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>Summary of changes (files changed, insertions, deletions)</returns>
	public static async Task<GitDiffSummary?> GetDiffSummaryAsync(
		string workingDirectory,
		string? baseCommit = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var diffTarget = string.IsNullOrEmpty(baseCommit) ? "HEAD" : baseCommit;
			var result = await RunGitCommandAsync(
				$"diff {diffTarget} --stat --shortstat",
				workingDirectory,
				cancellationToken);

			if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
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

	/// <summary>
	/// Gets a list of files that have been changed.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="baseCommit">Optional base commit to compare against</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>List of changed file paths</returns>
	public static async Task<IReadOnlyList<string>> GetChangedFilesAsync(
		string workingDirectory,
		string? baseCommit = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var diffTarget = string.IsNullOrEmpty(baseCommit) ? "HEAD" : baseCommit;
			var result = await RunGitCommandAsync(
				$"diff {diffTarget} --name-only",
				workingDirectory,
				cancellationToken);

			if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
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

	/// <summary>
	/// Checks if the specified directory is a git repository.
	/// </summary>
	public static async Task<bool> IsGitRepositoryAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await RunGitCommandAsync(
				"rev-parse --is-inside-work-tree",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 5);

			return result.ExitCode == 0 && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Checks if git is available on the system.
	/// </summary>
	public static async Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await RunGitCommandAsync(
				"--version",
				Directory.GetCurrentDirectory(),
				cancellationToken,
				timeoutSeconds: 5);

			return result.ExitCode == 0 && result.Output.Contains("git version");
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Checks if there are any uncommitted changes in the working directory.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>True if there are uncommitted changes, false otherwise</returns>
	public static async Task<bool> HasUncommittedChangesAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await RunGitCommandAsync(
				"status --porcelain",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Stages all changes and creates a commit with the specified message.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="commitMessage">The commit message</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>Result containing success status, commit hash, and any error message</returns>
	public static async Task<GitOperationResult> CommitAllChangesAsync(
		string workingDirectory,
		string commitMessage,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// First, stage all changes
			var addResult = await RunGitCommandAsync(
				"add -A",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (addResult.ExitCode != 0)
			{
				return new GitOperationResult
				{
					Success = false,
					Error = $"Failed to stage changes: {addResult.Error}"
				};
			}

			// Escape the commit message for command line
			var escapedMessage = commitMessage
				.Replace("\\", "\\\\")
				.Replace("\"", "\\\"");

			// Create the commit
			var commitResult = await RunGitCommandAsync(
				$"commit -m \"{escapedMessage}\"",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 30);

			if (commitResult.ExitCode != 0)
			{
				// Check if there's nothing to commit
				if (commitResult.Output.Contains("nothing to commit") ||
					commitResult.Error.Contains("nothing to commit"))
				{
					return new GitOperationResult
					{
						Success = false,
						Error = "Nothing to commit - no changes detected"
					};
				}

				return new GitOperationResult
				{
					Success = false,
					Error = $"Failed to commit: {commitResult.Error}"
				};
			}

			// Get the commit hash
			var commitHash = await GetCurrentCommitHashAsync(workingDirectory, cancellationToken);

			return new GitOperationResult
			{
				Success = true,
				CommitHash = commitHash,
				Output = commitResult.Output
			};
		}
		catch (OperationCanceledException)
		{
			return new GitOperationResult
			{
				Success = false,
				Error = "Operation was cancelled"
			};
		}
		catch (Exception ex)
		{
			return new GitOperationResult
			{
				Success = false,
				Error = $"Unexpected error: {ex.Message}"
			};
		}
	}

	/// <summary>
	/// Pushes commits to the remote repository.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="remoteName">The remote name (defaults to 'origin')</param>
	/// <param name="branchName">The branch name (defaults to current branch)</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>Result containing success status and any error message</returns>
	public static async Task<GitOperationResult> PushAsync(
		string workingDirectory,
		string remoteName = "origin",
		string? branchName = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// Get current branch if not specified
			if (string.IsNullOrEmpty(branchName))
			{
				var branchResult = await RunGitCommandAsync(
					"rev-parse --abbrev-ref HEAD",
					workingDirectory,
					cancellationToken,
					timeoutSeconds: 10);

				if (branchResult.ExitCode != 0 || string.IsNullOrWhiteSpace(branchResult.Output))
				{
					return new GitOperationResult
					{
						Success = false,
						Error = "Could not determine current branch"
					};
				}

				branchName = branchResult.Output.Trim();
			}

			// Push to remote - using a longer timeout since push can take time
			var pushResult = await RunGitCommandAsync(
				$"push {remoteName} {branchName}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 120);

			if (pushResult.ExitCode != 0)
			{
				// Check for common error scenarios
				var errorMessage = pushResult.Error;

				if (errorMessage.Contains("rejected"))
				{
					return new GitOperationResult
					{
						Success = false,
						Error = "Push was rejected. You may need to pull remote changes first."
					};
				}

				if (errorMessage.Contains("Permission denied") || errorMessage.Contains("authentication"))
				{
					return new GitOperationResult
					{
						Success = false,
						Error = "Authentication failed. Please check your credentials."
					};
				}

				if (errorMessage.Contains("remote") && errorMessage.Contains("not found"))
				{
					return new GitOperationResult
					{
						Success = false,
						Error = $"Remote '{remoteName}' not found."
					};
				}

				return new GitOperationResult
				{
					Success = false,
					Error = $"Push failed: {errorMessage}"
				};
			}

			return new GitOperationResult
			{
				Success = true,
				Output = !string.IsNullOrWhiteSpace(pushResult.Output)
					? pushResult.Output
					: pushResult.Error, // Git often writes success info to stderr
				BranchName = branchName,
				RemoteName = remoteName
			};
		}
		catch (OperationCanceledException)
		{
			return new GitOperationResult
			{
				Success = false,
				Error = "Push operation timed out or was cancelled"
			};
		}
		catch (Exception ex)
		{
			return new GitOperationResult
			{
				Success = false,
				Error = $"Unexpected error: {ex.Message}"
			};
		}
	}

	/// <summary>
	/// Commits all changes and pushes to the remote repository in a single operation.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="commitMessage">The commit message</param>
	/// <param name="remoteName">The remote name (defaults to 'origin')</param>
	/// <param name="progressCallback">Optional callback for progress updates</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>Result containing success status and any error message</returns>
	public static async Task<GitOperationResult> CommitAndPushAsync(
		string workingDirectory,
		string commitMessage,
		string remoteName = "origin",
		Action<string>? progressCallback = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			progressCallback?.Invoke("Checking for changes...");

			// Check if there are changes to commit
			var hasChanges = await HasUncommittedChangesAsync(workingDirectory, cancellationToken);
			if (!hasChanges)
			{
				return new GitOperationResult
				{
					Success = false,
					Error = "No changes to commit"
				};
			}

			progressCallback?.Invoke("Staging and committing changes...");

			// Commit the changes
			var commitResult = await CommitAllChangesAsync(workingDirectory, commitMessage, cancellationToken);
			if (!commitResult.Success)
			{
				return commitResult;
			}

			progressCallback?.Invoke("Pushing to remote repository...");

			// Push to remote
			var pushResult = await PushAsync(workingDirectory, remoteName, null, cancellationToken);
			if (!pushResult.Success)
			{
				// Commit succeeded but push failed
				return new GitOperationResult
				{
					Success = false,
					CommitHash = commitResult.CommitHash,
					Error = $"Commit succeeded (hash: {commitResult.CommitHash?[..7] ?? "unknown"}) but push failed: {pushResult.Error}"
				};
			}

			return new GitOperationResult
			{
				Success = true,
				CommitHash = commitResult.CommitHash,
				BranchName = pushResult.BranchName,
				RemoteName = pushResult.RemoteName,
				Output = $"Successfully committed and pushed to {pushResult.RemoteName}/{pushResult.BranchName}"
			};
		}
		catch (OperationCanceledException)
		{
			return new GitOperationResult
			{
				Success = false,
				Error = "Operation was cancelled or timed out"
			};
		}
		catch (Exception ex)
		{
			return new GitOperationResult
			{
				Success = false,
				Error = $"Unexpected error: {ex.Message}"
			};
		}
	}

	/// <summary>
	/// Gets the current branch name.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The branch name, or null if not available</returns>
	public static async Task<string?> GetCurrentBranchAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await RunGitCommandAsync(
				"rev-parse --abbrev-ref HEAD",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
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

	/// <summary>
	/// Gets the remote URL for the specified remote.
	/// </summary>
	/// <param name="workingDirectory">The repository working directory</param>
	/// <param name="remoteName">The remote name (defaults to 'origin')</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The remote URL, or null if not available</returns>
	public static async Task<string?> GetRemoteUrlAsync(
		string workingDirectory,
		string remoteName = "origin",
		CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await RunGitCommandAsync(
				$"remote get-url {remoteName}",
				workingDirectory,
				cancellationToken,
				timeoutSeconds: 10);

			if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
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

	private static async Task<GitCommandResult> RunGitCommandAsync(
		string arguments,
		string workingDirectory,
		CancellationToken cancellationToken,
		int timeoutSeconds = DefaultTimeoutSeconds)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = PlatformHelper.IsWindows ? "git.exe" : "git",
			Arguments = arguments,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

		using var process = new Process { StartInfo = startInfo };
		var outputBuilder = new StringBuilder();
		var errorBuilder = new StringBuilder();

		process.OutputDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				outputBuilder.AppendLine(e.Data);
			}
		};

		process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				errorBuilder.AppendLine(e.Data);
			}
		};

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

		try
		{
			await process.WaitForExitAsync(timeoutCts.Token);
		}
		catch (OperationCanceledException)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch { }

			throw;
		}

		return new GitCommandResult
		{
			ExitCode = process.ExitCode,
			Output = outputBuilder.ToString(),
			Error = errorBuilder.ToString()
		};
	}

	private static string TruncateDiff(string diff, int maxBytes)
	{
		var encoding = Encoding.UTF8;
		var currentBytes = 0;
		var lines = diff.Split('\n');
		var resultBuilder = new StringBuilder();

		foreach (var line in lines)
		{
			var lineBytes = encoding.GetByteCount(line) + 1; // +1 for newline
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
		var summary = new GitDiffSummary();
		var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		foreach (var line in lines)
		{
			// Parse the shortstat line: "X files changed, Y insertions(+), Z deletions(-)"
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
							summary.FilesChanged = files;
						}
					}
					else if (trimmed.Contains("insertion"))
					{
						var numStr = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
						if (int.TryParse(numStr, out var insertions))
						{
							summary.Insertions = insertions;
						}
					}
					else if (trimmed.Contains("deletion"))
					{
						var numStr = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
						if (int.TryParse(numStr, out var deletions))
						{
							summary.Deletions = deletions;
						}
					}
				}
			}
		}

		return summary;
	}

	private struct GitCommandResult
	{
		public int ExitCode { get; set; }
		public string Output { get; set; }
		public string Error { get; set; }
	}
}

/// <summary>
/// Summary of git diff statistics
/// </summary>
public class GitDiffSummary
{
	public int FilesChanged { get; set; }
	public int Insertions { get; set; }
	public int Deletions { get; set; }

	public override string ToString()
	{
		return $"{FilesChanged} file(s) changed, {Insertions} insertion(s)(+), {Deletions} deletion(s)(-)";
	}
}

/// <summary>
/// Result of a git operation (commit, push, etc.)
/// </summary>
public class GitOperationResult
{
	/// <summary>
	/// Whether the operation succeeded
	/// </summary>
	public bool Success { get; set; }

	/// <summary>
	/// Error message if the operation failed
	/// </summary>
	public string? Error { get; set; }

	/// <summary>
	/// Output from the git command
	/// </summary>
	public string? Output { get; set; }

	/// <summary>
	/// Commit hash if a commit was created
	/// </summary>
	public string? CommitHash { get; set; }

	/// <summary>
	/// Branch name involved in the operation
	/// </summary>
	public string? BranchName { get; set; }

	/// <summary>
	/// Remote name involved in the operation
	/// </summary>
	public string? RemoteName { get; set; }
}
