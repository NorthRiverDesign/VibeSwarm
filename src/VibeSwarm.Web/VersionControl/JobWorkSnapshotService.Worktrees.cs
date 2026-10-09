using Microsoft.Extensions.Logging;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Work an agent does in a git worktree of its own is invisible to delivery, which only looks at
/// the project checkout. These methods find the worktrees a run left behind and bring their work
/// home, so a job's changes are never stranded in a directory nobody looks at.
/// </summary>
public sealed partial class JobWorkSnapshotService
{
	/// <summary>
	/// Paths of the repository's linked worktrees, not counting the checkout itself.
	/// </summary>
	public async Task<IReadOnlyList<string>> ListWorktreesAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		var result = await _git.ExecuteAsync("worktree list --porcelain", workingDirectory, cancellationToken);
		if (!result.Success)
		{
			return [];
		}

		return result.Output
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
			.Select(line => line["worktree ".Length..].Trim())
			.Skip(1)
			.ToList();
	}

	/// <summary>
	/// Commits whatever is uncommitted in <paramref name="worktreePath"/> and removes the worktree.
	/// Returns the branch that now holds its work, or null when it held nothing the checkout
	/// doesn't already have.
	/// </summary>
	public async Task<string?> CollectWorktreeAsync(
		string workingDirectory,
		string worktreePath,
		Guid jobId,
		CancellationToken cancellationToken = default)
	{
		var shortId = jobId.ToString("N")[..8];
		var status = await _git.ExecuteAsync("status --porcelain", worktreePath, cancellationToken);
		if (status.Success && !string.IsNullOrWhiteSpace(status.Output))
		{
			await _git.ExecuteAsync("add -A", worktreePath, cancellationToken, timeoutSeconds: 120);
			await _git.ExecuteAsync(
				$"-c user.name=\"{AppConstants.AppName}\" -c user.email=\"vibeswarm@localhost\" commit -q --no-verify -m \"{AppConstants.AppName}: work left in a worktree by job {shortId}\"",
				worktreePath,
				cancellationToken,
				timeoutSeconds: 120);
		}

		var worktreeHead = await RevParseAsync(worktreePath, "HEAD", cancellationToken);
		if (worktreeHead == null)
		{
			_logger.LogWarning("Could not read worktree {Worktree} left by job {JobId}; leaving it in place", worktreePath, jobId);
			return null;
		}

		var branchResult = await _git.ExecuteAsync("rev-parse --abbrev-ref HEAD", worktreePath, cancellationToken);
		var branch = branchResult.Success ? branchResult.Output.Trim() : "HEAD";
		var aheadResult = await _git.ExecuteAsync($"rev-list --count HEAD..{worktreeHead}", workingDirectory, cancellationToken);
		var hasWork = aheadResult.Success && int.TryParse(aheadResult.Output.Trim(), out var ahead) && ahead > 0;

		// A detached worktree has no branch to keep its commits once the directory is gone.
		if (hasWork && branch == "HEAD")
		{
			branch = $"vibeswarm/recovery/worktree-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{shortId}";
			var create = await _git.ExecuteAsync($"branch {branch} {worktreeHead}", workingDirectory, cancellationToken);
			if (!create.Success)
			{
				_logger.LogWarning("Could not keep the work in worktree {Worktree} for job {JobId}; leaving it in place", worktreePath, jobId);
				return null;
			}
		}

		var remove = await _git.ExecuteAsync($"worktree remove --force \"{worktreePath}\"", workingDirectory, cancellationToken, timeoutSeconds: 60);
		if (!remove.Success)
		{
			_logger.LogWarning("Could not remove worktree {Worktree} left by job {JobId}: {Error}", worktreePath, jobId, remove.Error.Trim());
		}

		if (!hasWork)
		{
			if (branch != "HEAD")
			{
				// -d only deletes a branch whose commits are all on HEAD already.
				await _git.ExecuteAsync($"branch -d {branch}", workingDirectory, cancellationToken);
			}

			return null;
		}

		_logger.LogInformation("Job {JobId} left work in worktree {Worktree}; it is on branch {Branch}", jobId, worktreePath, branch);
		return branch;
	}

	/// <summary>
	/// Applies the work on <paramref name="branch"/> to a clean checkout as uncommitted changes, as if
	/// the agent had made them there, and deletes the branch. On a conflict the checkout is put back
	/// as it was and the branch is kept.
	/// </summary>
	public async Task<bool> MergeIntoCleanCheckoutAsync(string workingDirectory, string branch, CancellationToken cancellationToken = default)
	{
		var merge = await _git.ExecuteAsync($"merge --squash {branch}", workingDirectory, cancellationToken, timeoutSeconds: 120);
		if (!merge.Success)
		{
			// The checkout was clean before the merge, so resetting it loses nothing.
			await _git.ExecuteAsync("reset -q --hard HEAD", workingDirectory, cancellationToken, timeoutSeconds: 60);
			return false;
		}

		await _git.ExecuteAsync("reset -q", workingDirectory, cancellationToken, timeoutSeconds: 60);
		await _git.ExecuteAsync($"branch -D {branch}", workingDirectory, cancellationToken);
		return true;
	}
}
