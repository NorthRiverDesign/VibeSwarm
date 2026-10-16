using System.Globalization;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Git steps behind deploying a development branch into a release branch: what is waiting to go
/// out, and the merge a provider finishes in the job checkout when the two branches conflict.
/// </summary>
public sealed class ReleaseBranchGit
{
	private readonly IGitCommandExecutor _git;

	public ReleaseBranchGit(IGitCommandExecutor git)
	{
		_git = git;
	}

	/// <summary>
	/// The branch as origin has it when origin has it, otherwise the local branch, otherwise null.
	/// </summary>
	public async Task<string?> ResolveRefAsync(string workingDirectory, string branch, CancellationToken cancellationToken = default)
	{
		foreach (var candidate in new[] { $"refs/remotes/origin/{branch}", $"refs/heads/{branch}" })
		{
			var result = await _git.ExecuteAsync($"rev-parse --verify -q \"{candidate}\"", workingDirectory, cancellationToken);
			if (result.Success)
			{
				return candidate.StartsWith("refs/remotes/", StringComparison.Ordinal) ? $"origin/{branch}" : branch;
			}
		}

		return null;
	}

	/// <summary>Commits reachable from <paramref name="toRef"/> but not <paramref name="fromRef"/>, newest first.</summary>
	public async Task<(int Count, List<ReleaseChange> Latest)> GetChangesAsync(
		string workingDirectory, string fromRef, string toRef, int max, CancellationToken cancellationToken = default)
	{
		var range = $"\"{fromRef}..{toRef}\"";
		var count = await _git.ExecuteAsync($"rev-list --count {range}", workingDirectory, cancellationToken);
		if (!count.Success || !int.TryParse(count.Output.Trim(), out var total) || total == 0)
		{
			return (0, []);
		}

		var log = await _git.ExecuteAsync($"log -n {max} --format=%H%x1f%s%x1f%cI {range}", workingDirectory, cancellationToken);
		return (total, log.Success ? ParseLog(log.Output) : []);
	}

	public async Task<ReleaseChange?> GetCommitAsync(string workingDirectory, string reference, CancellationToken cancellationToken = default)
	{
		var log = await _git.ExecuteAsync($"log -n 1 --format=%H%x1f%s%x1f%cI \"{reference}\"", workingDirectory, cancellationToken);
		return log.Success ? ParseLog(log.Output).FirstOrDefault() : null;
	}

	/// <summary>
	/// Merges <paramref name="branch"/> into the checked-out branch without committing, so an agent
	/// can resolve the conflicts and VibeSwarm commits the result as the merge. When the tree
	/// already holds an earlier run's restored work, that work is the merge in progress: the merge
	/// is recorded around it instead of started again.
	/// </summary>
	public async Task<BranchMergeStart> StartMergeAsync(string workingDirectory, string branch, CancellationToken cancellationToken = default)
	{
		var sourceRef = await ResolveRefAsync(workingDirectory, branch, cancellationToken);
		if (sourceRef == null)
		{
			return BranchMergeStart.Failed($"The branch '{branch}' was not found locally or on origin.");
		}

		var alreadyMerged = await _git.ExecuteAsync($"merge-base --is-ancestor \"{sourceRef}\" HEAD", workingDirectory, cancellationToken);
		if (alreadyMerged.Success)
		{
			return new BranchMergeStart(BranchMergeOutcome.AlreadyMerged, sourceRef, [], null);
		}

		var status = await _git.ExecuteAsync("status --porcelain --untracked-files=all", workingDirectory, cancellationToken);
		if (!status.Success)
		{
			return BranchMergeStart.Failed(FirstLine(status.Error));
		}

		if (!string.IsNullOrWhiteSpace(status.Output))
		{
			return await RecordMergeAroundRestoredWorkAsync(workingDirectory, branch, sourceRef, status.Output, cancellationToken);
		}

		var merge = await _git.ExecuteAsync($"merge --no-ff --no-commit \"{sourceRef}\"", workingDirectory, cancellationToken, timeoutSeconds: 120);
		if (merge.Success)
		{
			return new BranchMergeStart(BranchMergeOutcome.Merged, sourceRef, [], null);
		}

		var conflicted = await ListLinesAsync(workingDirectory, "diff --name-only --diff-filter=U", cancellationToken);
		if (conflicted.Count > 0)
		{
			return new BranchMergeStart(BranchMergeOutcome.Conflicted, sourceRef, conflicted, null);
		}

		await _git.ExecuteAsync("merge --abort", workingDirectory, cancellationToken);
		return BranchMergeStart.Failed(FirstLine(string.IsNullOrWhiteSpace(merge.Error) ? merge.Output : merge.Error));
	}

	private async Task<BranchMergeStart> RecordMergeAroundRestoredWorkAsync(
		string workingDirectory, string branch, string sourceRef, string statusOutput, CancellationToken cancellationToken)
	{
		var commit = await _git.ExecuteAsync($"rev-parse --verify \"{sourceRef}^{{commit}}\"", workingDirectory, cancellationToken);
		var mergeHeadPath = await _git.ExecuteAsync("rev-parse --git-path MERGE_HEAD", workingDirectory, cancellationToken);
		var mergeMsgPath = await _git.ExecuteAsync("rev-parse --git-path MERGE_MSG", workingDirectory, cancellationToken);
		if (!commit.Success || !mergeHeadPath.Success || !mergeMsgPath.Success)
		{
			return BranchMergeStart.Failed("Couldn't record the merge around the restored work.");
		}

		await File.WriteAllTextAsync(Path.Combine(workingDirectory, mergeHeadPath.Output.Trim()), commit.Output.Trim() + "\n", cancellationToken);
		await File.WriteAllTextAsync(Path.Combine(workingDirectory, mergeMsgPath.Output.Trim()), $"Merge branch '{branch}'\n", cancellationToken);

		// The restored work may be a resolution the earlier run hadn't finished.
		var changedFiles = statusOutput
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Where(line => line.Length > 3)
			.Select(line => line[3..].Trim().Trim('"'))
			.Select(path => path.Contains(" -> ", StringComparison.Ordinal) ? path[(path.IndexOf(" -> ", StringComparison.Ordinal) + 4)..] : path)
			.ToList();
		var withMarkers = changedFiles.Where(file => HasConflictMarkers(Path.Combine(workingDirectory, file))).ToList();
		return new BranchMergeStart(withMarkers.Count > 0 ? BranchMergeOutcome.Conflicted : BranchMergeOutcome.Merged, sourceRef, withMarkers, null);
	}

	private static bool HasConflictMarkers(string path)
	{
		try
		{
			var info = new FileInfo(path);
			return info.Exists && info.Length <= 5 * 1024 * 1024 && File.ReadLines(path).Any(line =>
				line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line.StartsWith(">>>>>>> ", StringComparison.Ordinal));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private async Task<List<string>> ListLinesAsync(string workingDirectory, string arguments, CancellationToken cancellationToken)
	{
		var result = await _git.ExecuteAsync(arguments, workingDirectory, cancellationToken);
		return result.Success
			? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList()
			: [];
	}

	private static List<ReleaseChange> ParseLog(string output) => output
		.Split('\n', StringSplitOptions.RemoveEmptyEntries)
		.Select(line => line.Split('\x1f'))
		.Where(parts => parts.Length == 3)
		.Select(parts => new ReleaseChange
		{
			Hash = parts[0].Trim(),
			Subject = parts[1].Trim(),
			CommittedAt = DateTimeOffset.TryParse(parts[2].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
				? at.UtcDateTime
				: default
		})
		.ToList();

	private static string FirstLine(string? text) =>
		text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "git merge failed.";
}
