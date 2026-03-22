using System.Text;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

public sealed partial class VersionControlService
{
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
