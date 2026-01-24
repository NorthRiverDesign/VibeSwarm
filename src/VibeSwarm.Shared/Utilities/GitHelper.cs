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
