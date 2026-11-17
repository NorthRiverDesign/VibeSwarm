using Microsoft.Extensions.Logging;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Git worktrees that let the members of a team swarm work at the same time. Each member gets a
/// worktree of its own on a <c>vibeswarm/swarm/</c> branch, cut from the project's freshly synced
/// branch, so no agent sees or overwrites another's files. When a member finishes, its commits are
/// replayed onto the project's branch in the checkout, one member at a time; work that overlaps
/// what got there first stays on its branch for a merge job.
/// </summary>
public sealed class SwarmWorktrees
{
	public const string BranchPrefix = "vibeswarm/swarm/";

	private readonly IGitCommandExecutor _git;
	private readonly ILogger<SwarmWorktrees> _logger;
	private readonly string _root;

	public SwarmWorktrees(IGitCommandExecutor git, ILogger<SwarmWorktrees> logger)
		: this(git, logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSwarm", "worktrees"))
	{
	}

	/// <param name="rootDirectory">Where worktrees go. Outside every repository, so tools in the
	/// checkout never pick up a worktree's files and an agent in a worktree never finds the checkout's.</param>
	public SwarmWorktrees(IGitCommandExecutor git, ILogger<SwarmWorktrees> logger, string rootDirectory)
	{
		_git = git;
		_logger = logger;
		_root = rootDirectory;
	}

	public string GetPath(Guid projectId, Guid jobId) =>
		Path.Combine(GetProjectDirectory(projectId), ShortId(jobId));

	/// <summary>
	/// Whether <paramref name="path"/> is one of these worktrees. They belong to their swarm member,
	/// not to whichever job happens to see them, so they are never collected as a run's leftovers.
	/// </summary>
	public bool IsSwarmWorktree(string path)
	{
		var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
	}

	/// <summary>
	/// The job's worktree, ready to work in. One kept from an interrupted run is used as it is, so
	/// nothing it holds is lost; otherwise a new one is cut from the checkout's HEAD.
	/// </summary>
	public async Task<SwarmWorktree> OpenAsync(string checkout, Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
	{
		var path = GetPath(projectId, jobId);
		if (await IsWorktreeAsync(path, cancellationToken))
		{
			return new SwarmWorktree(path, await CurrentBranchAsync(path, cancellationToken), Reused: true);
		}

		// A directory git no longer knows as a worktree: set it aside rather than delete
		// whatever it holds, and drop git's record of it.
		if (Directory.Exists(path))
		{
			var aside = $"{path}-stale-{DateTime.UtcNow:yyyyMMddHHmmss}";
			Directory.Move(path, aside);
			_logger.LogWarning("Moved an unusable swarm worktree for job {JobId} aside to {Path}", jobId, aside);
		}

		await _git.ExecuteAsync("worktree prune", checkout, cancellationToken);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		var branch = $"{BranchPrefix}{ShortId(jobId)}-{DateTime.UtcNow:yyyyMMddHHmmss}";
		var add = await _git.ExecuteAsync($"worktree add -q -b {branch} \"{path}\" HEAD", checkout, cancellationToken, timeoutSeconds: 300);
		if (!add.Success)
		{
			throw new InvalidOperationException(
				$"Couldn't create a worktree for the agent: {FirstLine(add.Error, add.Output)} " +
				"Agents of a team work in worktrees of their own, which needs at least one commit in the repository.");
		}

		_logger.LogInformation("Created swarm worktree {Path} on {Branch} for job {JobId}", path, branch, jobId);
		return new SwarmWorktree(path, branch, Reused: false);
	}

	/// <summary>
	/// Replays the worktree's commits onto the checkout's HEAD and fast-forwards the checkout to
	/// them. On a conflict both are put back as they were.
	/// </summary>
	public async Task<SwarmIntegration> IntegrateAsync(string checkout, string worktreePath, CancellationToken cancellationToken = default)
	{
		var target = await RevParseAsync(checkout, "HEAD", cancellationToken);
		var work = await RevParseAsync(worktreePath, "HEAD", cancellationToken);
		if (target == null || work == null)
		{
			return SwarmIntegration.Failed("Couldn't read the checkout's or the worktree's HEAD.");
		}

		var alreadyThere = await _git.ExecuteAsync($"merge-base --is-ancestor {work} {target}", checkout, cancellationToken);
		if (alreadyThere.Success)
		{
			return new SwarmIntegration(SwarmIntegrationOutcome.Integrated, target, [], null);
		}

		var rebase = await _git.ExecuteAsync($"rebase -q {target}", worktreePath, cancellationToken, timeoutSeconds: 300);
		if (!rebase.Success)
		{
			var conflicted = await ListLinesAsync(worktreePath, "diff --name-only --diff-filter=U", cancellationToken);
			await _git.ExecuteAsync("rebase --abort", worktreePath, cancellationToken, timeoutSeconds: 60);
			return conflicted.Count > 0
				? new SwarmIntegration(SwarmIntegrationOutcome.Conflicted, null, conflicted, null)
				: SwarmIntegration.Failed(FirstLine(rebase.Error, rebase.Output));
		}

		var rebased = await RevParseAsync(worktreePath, "HEAD", cancellationToken);
		var fastForward = await _git.ExecuteAsync($"merge -q --ff-only {rebased}", checkout, cancellationToken, timeoutSeconds: 120);
		if (rebased == null || !fastForward.Success)
		{
			return SwarmIntegration.Failed(FirstLine(fastForward.Error, fastForward.Output));
		}

		return new SwarmIntegration(SwarmIntegrationOutcome.Integrated, rebased, [], null);
	}

	/// <summary>
	/// Commits whatever the worktree holds uncommitted to its branch and removes the worktree.
	/// Returns the branch when it holds work the checkout doesn't have; otherwise the branch is
	/// deleted and the result is null.
	/// </summary>
	public async Task<string?> ReleaseAsync(string checkout, string worktreePath, string label, CancellationToken cancellationToken = default)
	{
		var status = await _git.ExecuteAsync("status --porcelain", worktreePath, cancellationToken);
		if (status.Success && !string.IsNullOrWhiteSpace(status.Output))
		{
			await _git.ExecuteAsync("add -A", worktreePath, cancellationToken, timeoutSeconds: 120);
			await _git.ExecuteAsync(
				$"-c user.name=\"{AppConstants.AppName}\" -c user.email=\"vibeswarm@localhost\" commit -q --no-verify -m \"{AppConstants.AppName}: work left by job {label}\"",
				worktreePath,
				cancellationToken,
				timeoutSeconds: 120);
		}

		var head = await RevParseAsync(worktreePath, "HEAD", cancellationToken);
		if (head == null)
		{
			_logger.LogWarning("Could not read the swarm worktree {Path}; leaving it in place", worktreePath);
			return null;
		}

		// Commits the checkout has an equivalent of (marked "-") are delivered, even when a push
		// replayed them under new hashes.
		var branch = await CurrentBranchAsync(worktreePath, cancellationToken);
		var undelivered = await _git.ExecuteAsync($"cherry HEAD {head}", checkout, cancellationToken, timeoutSeconds: 120);
		var hasWork = !undelivered.Success || undelivered.Output
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(line => line.StartsWith('+'));

		// A detached worktree has no branch to keep its commits once the directory is gone.
		if (hasWork && branch == "HEAD")
		{
			branch = $"{BranchPrefix}{label}-{DateTime.UtcNow:yyyyMMddHHmmss}";
			var create = await _git.ExecuteAsync($"branch {branch} {head}", checkout, cancellationToken);
			if (!create.Success)
			{
				_logger.LogWarning("Could not keep the work in swarm worktree {Path}; leaving it in place", worktreePath);
				return null;
			}
		}

		var remove = await _git.ExecuteAsync($"worktree remove --force \"{worktreePath}\"", checkout, cancellationToken, timeoutSeconds: 120);
		if (!remove.Success)
		{
			_logger.LogWarning("Could not remove swarm worktree {Path}: {Error}", worktreePath, remove.Error.Trim());
		}

		if (!hasWork)
		{
			if (branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
			{
				await _git.ExecuteAsync($"branch -D {branch}", checkout, cancellationToken);
			}

			return null;
		}

		_logger.LogInformation("Kept the work of swarm worktree {Path} on branch {Branch}", worktreePath, branch);
		return branch;
	}

	/// <summary>
	/// Releases the project's worktrees that no live job owns any more, such as one kept for a
	/// paused job that was then cancelled. Their work goes to branches; returns those branches.
	/// </summary>
	public async Task<IReadOnlyList<string>> SweepAsync(
		string checkout,
		Guid projectId,
		IReadOnlyCollection<Guid> liveJobIds,
		CancellationToken cancellationToken = default)
	{
		var directory = GetProjectDirectory(projectId);
		if (!Directory.Exists(directory))
		{
			return [];
		}

		var live = liveJobIds.Select(ShortId).ToHashSet(StringComparer.Ordinal);
		var kept = new List<string>();
		foreach (var path in Directory.GetDirectories(directory))
		{
			var label = Path.GetFileName(path);
			if (live.Contains(label) || !await IsWorktreeAsync(path, cancellationToken))
			{
				continue;
			}

			if (await ReleaseAsync(checkout, path, label, cancellationToken) is { } branch)
			{
				kept.Add(branch);
			}
		}

		return kept;
	}

	/// <summary>
	/// Deletes a swarm branch once its work is on the checkout's HEAD, as after its merge job.
	/// </summary>
	public async Task DeleteBranchIfMergedAsync(string checkout, string branch, CancellationToken cancellationToken = default)
	{
		if (!branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
		{
			return;
		}

		var delete = await _git.ExecuteAsync($"branch -d {branch}", checkout, cancellationToken);
		if (delete.Success)
		{
			_logger.LogInformation("Deleted the merged swarm branch {Branch}", branch);
		}
	}

	private string GetProjectDirectory(Guid projectId) => Path.Combine(_root, ShortId(projectId));

	private static string ShortId(Guid id) => id.ToString("N")[..8];

	private async Task<bool> IsWorktreeAsync(string path, CancellationToken cancellationToken)
	{
		if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, ".git")))
		{
			return false;
		}

		var topLevel = await _git.ExecuteAsync("rev-parse --show-toplevel", path, cancellationToken);
		return topLevel.Success && string.Equals(
			Path.GetFullPath(topLevel.Output.Trim()).TrimEnd(Path.DirectorySeparatorChar),
			Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
			StringComparison.Ordinal);
	}

	private async Task<string> CurrentBranchAsync(string path, CancellationToken cancellationToken)
	{
		var result = await _git.ExecuteAsync("rev-parse --abbrev-ref HEAD", path, cancellationToken);
		return result.Success ? result.Output.Trim() : "HEAD";
	}

	private async Task<string?> RevParseAsync(string path, string revision, CancellationToken cancellationToken)
	{
		var result = await _git.ExecuteAsync($"rev-parse --verify -q {revision}", path, cancellationToken);
		return result.Success && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : null;
	}

	private async Task<List<string>> ListLinesAsync(string path, string arguments, CancellationToken cancellationToken)
	{
		var result = await _git.ExecuteAsync(arguments, path, cancellationToken);
		return result.Success
			? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
			: [];
	}

	private static string FirstLine(string? error, string? output)
	{
		var text = string.IsNullOrWhiteSpace(error) ? output : error;
		return text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
			?? "git reported no detail.";
	}
}

public sealed record SwarmWorktree(string Path, string Branch, bool Reused);

public enum SwarmIntegrationOutcome
{
	Integrated,
	Conflicted,
	Failed
}

/// <param name="CommitHash">The checkout's HEAD once the work is on it.</param>
public sealed record SwarmIntegration(
	SwarmIntegrationOutcome Outcome,
	string? CommitHash,
	IReadOnlyList<string> ConflictedFiles,
	string? Error)
{
	public static SwarmIntegration Failed(string error) => new(SwarmIntegrationOutcome.Failed, null, [], error);
}
