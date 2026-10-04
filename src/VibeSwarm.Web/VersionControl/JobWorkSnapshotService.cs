using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Keeps a job's work safe between its runs. Every job starts by resetting the checkout to the
/// remote, which drops whatever an earlier run left uncommitted or unpushed. At the end of a run
/// the work is saved as a commit under <c>refs/vibeswarm/jobs/</c>, outside any branch, and a
/// follow-up re-applies it on top of the freshly synced branch so both land together.
/// </summary>
public sealed partial class JobWorkSnapshotService
{
	public const string RefPrefix = "refs/vibeswarm/jobs";

	private const long MaxConflictScanBytes = 5 * 1024 * 1024;

	private readonly IGitCommandExecutor _git;
	private readonly ILogger<JobWorkSnapshotService> _logger;

	public JobWorkSnapshotService(IGitCommandExecutor git, ILogger<JobWorkSnapshotService> logger)
	{
		_git = git;
		_logger = logger;
	}

	/// <summary>
	/// Saves the working tree (tracked and untracked files, minus ignored ones) as a commit whose
	/// parent is <paramref name="baseCommit"/>, leaving HEAD and the files untouched. Returns null
	/// when there is nothing to keep: the run changed nothing, or its work is already on a remote branch.
	/// </summary>
	public async Task<string?> SaveAsync(string workingDirectory, Guid jobId, string baseCommit, CancellationToken cancellationToken = default)
	{
		if (!IsCommitHash(baseCommit))
		{
			return null;
		}

		var status = await _git.ExecuteAsync("status --porcelain", workingDirectory, cancellationToken);
		var head = await RevParseAsync(workingDirectory, "HEAD", cancellationToken);
		if (!status.Success || head == null)
		{
			return null;
		}

		if (string.IsNullOrWhiteSpace(status.Output) &&
			(string.Equals(head, baseCommit, StringComparison.OrdinalIgnoreCase) ||
				await IsOnRemoteBranchAsync(workingDirectory, head, cancellationToken)))
		{
			return null;
		}

		string? tree;
		try
		{
			var add = await _git.ExecuteAsync("add -A", workingDirectory, cancellationToken, timeoutSeconds: 120);
			if (!add.Success)
			{
				_logger.LogWarning("Could not stage the work of job {JobId} for its snapshot: {Error}", jobId, add.Error.Trim());
				return null;
			}

			var writeTree = await _git.ExecuteAsync("write-tree", workingDirectory, cancellationToken, timeoutSeconds: 60);
			tree = writeTree.Success ? writeTree.Output.Trim() : null;
		}
		finally
		{
			// Unstage again: the files stay exactly as the run left them.
			await _git.ExecuteAsync("reset -q", workingDirectory, CancellationToken.None, timeoutSeconds: 60);
		}

		if (!IsCommitHash(tree) ||
			string.Equals(tree, await RevParseAsync(workingDirectory, $"{baseCommit}^{{tree}}", cancellationToken), StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var shortId = jobId.ToString("N")[..8];
		var commitTree = await _git.ExecuteAsync(
			$"-c user.name=\"{AppConstants.AppName}\" -c user.email=\"vibeswarm@localhost\" commit-tree {tree} -p {baseCommit} -m \"{AppConstants.AppName} work from job {shortId}\"",
			workingDirectory,
			cancellationToken);
		var snapshot = commitTree.Success ? commitTree.Output.Trim() : null;
		if (!IsCommitHash(snapshot))
		{
			_logger.LogWarning("Could not create the work snapshot for job {JobId}: {Error}", jobId, commitTree.Error.Trim());
			return null;
		}

		var refName = $"{RefPrefix}/{jobId:N}/{snapshot[..12]}";
		var updateRef = await _git.ExecuteAsync($"update-ref {refName} {snapshot}", workingDirectory, cancellationToken);
		if (!updateRef.Success)
		{
			_logger.LogWarning("Could not keep the work snapshot for job {JobId} under {Ref}: {Error}", jobId, refName, updateRef.Error.Trim());
			return null;
		}

		return snapshot;
	}

	/// <summary>
	/// Whether <paramref name="commit"/> is part of the checked-out branch's history.
	/// </summary>
	public async Task<bool> IsInHeadAsync(string workingDirectory, string commit, CancellationToken cancellationToken = default)
	{
		if (!IsCommitHash(commit))
		{
			return false;
		}

		var result = await _git.ExecuteAsync($"merge-base --is-ancestor {commit} HEAD", workingDirectory, cancellationToken);
		return result.Success;
	}

	/// <summary>
	/// Re-applies a snapshot from <see cref="SaveAsync"/> to the working tree as uncommitted
	/// changes. When the branch has moved on and the two disagree, the affected files keep git's
	/// conflict markers for the agent to resolve.
	/// </summary>
	public async Task<JobWorkRestoreResult> RestoreAsync(string workingDirectory, string snapshotCommit, CancellationToken cancellationToken = default)
	{
		if (!IsCommitHash(snapshotCommit))
		{
			return JobWorkRestoreResult.Failed($"'{snapshotCommit}' is not a commit hash.");
		}

		var exists = await _git.ExecuteAsync($"cat-file -e {snapshotCommit}^{{commit}}", workingDirectory, cancellationToken);
		if (!exists.Success)
		{
			return JobWorkRestoreResult.Failed($"The saved work ({snapshotCommit[..8]}) is no longer in this repository.");
		}

		var pick = await _git.ExecuteAsync($"cherry-pick --no-commit {snapshotCommit}", workingDirectory, cancellationToken, timeoutSeconds: 120);
		var conflicted = pick.Success
			? []
			: await ListPathsAsync(workingDirectory, "diff --name-only --diff-filter=U -z", cancellationToken);
		if (!pick.Success && conflicted.Count == 0)
		{
			var error = string.IsNullOrWhiteSpace(pick.Error) ? pick.Output : pick.Error;
			return JobWorkRestoreResult.Failed(error.Trim());
		}

		// Unstage, which also clears the unmerged entries, so the restored work looks like any
		// other uncommitted edit. Conflict markers stay in the files.
		await _git.ExecuteAsync("reset -q", workingDirectory, cancellationToken, timeoutSeconds: 60);

		var files = await ListPathsAsync(workingDirectory, "status --porcelain -z --untracked-files=all", cancellationToken, statusEntries: true);
		if (files.Count == 0)
		{
			return JobWorkRestoreResult.AlreadyOnBranch();
		}

		return new JobWorkRestoreResult
		{
			Outcome = conflicted.Count > 0 ? JobWorkRestoreOutcome.Conflicted : JobWorkRestoreOutcome.Restored,
			Files = files,
			ConflictedFiles = conflicted
		};
	}

	/// <summary>
	/// Returns the files among <paramref name="files"/> that still contain conflict markers.
	/// </summary>
	public async Task<IReadOnlyList<string>> FindUnresolvedConflictsAsync(string workingDirectory, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
	{
		if (files.Count == 0)
		{
			return [];
		}

		var topLevel = await _git.ExecuteAsync("rev-parse --show-toplevel", workingDirectory, cancellationToken);
		var root = topLevel.Success && !string.IsNullOrWhiteSpace(topLevel.Output) ? topLevel.Output.Trim() : workingDirectory;

		return files.Where(file => HasConflictMarkers(Path.Combine(root, file))).ToList();
	}

	private static bool HasConflictMarkers(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length > MaxConflictScanBytes)
			{
				return false;
			}

			return File.ReadLines(path).Any(line => line.StartsWith("<<<<<<< ", StringComparison.Ordinal) ||
				line.StartsWith(">>>>>>> ", StringComparison.Ordinal));
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private async Task<bool> IsOnRemoteBranchAsync(string workingDirectory, string commit, CancellationToken cancellationToken)
	{
		var result = await _git.ExecuteAsync($"branch -r --contains {commit}", workingDirectory, cancellationToken);
		return result.Success && !string.IsNullOrWhiteSpace(result.Output);
	}

	private async Task<string?> RevParseAsync(string workingDirectory, string revision, CancellationToken cancellationToken)
	{
		var result = await _git.ExecuteAsync($"rev-parse --verify -q {revision}", workingDirectory, cancellationToken);
		var hash = result.Output.Trim();
		return result.Success && IsCommitHash(hash) ? hash : null;
	}

	private async Task<IReadOnlyList<string>> ListPathsAsync(string workingDirectory, string arguments, CancellationToken cancellationToken, bool statusEntries = false)
	{
		var result = await _git.ExecuteAsync(arguments, workingDirectory, cancellationToken);
		if (!result.Success)
		{
			return [];
		}

		return result.Output
			.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.Select(entry => entry.Trim('\r', '\n'))
			.Where(entry => entry.Length > (statusEntries ? 3 : 0))
			.Select(entry => statusEntries ? entry[3..] : entry)
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	private static bool IsCommitHash([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value)
		=> !string.IsNullOrWhiteSpace(value) && CommitHashPattern().IsMatch(value);

	[GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
	private static partial Regex CommitHashPattern();
}
