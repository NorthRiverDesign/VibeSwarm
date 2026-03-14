using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public enum ProjectChangesSourceType
{
	None = 0,
	WorkingDirectory = 1,
	Commit = 2
}

public sealed record ProjectChangesSelection(
	ProjectChangesSourceType SourceType,
	string? CommitHash,
	string? BaseCommit,
	IReadOnlyList<Job> LinkedJobs)
{
	public bool HasChanges => SourceType != ProjectChangesSourceType.None;
}

public static class ProjectChangesSectionResolver
{
	public static ProjectChangesSelection Resolve(IReadOnlyList<Job>? jobs, bool hasUncommittedChanges)
	{
		jobs ??= [];

		if (hasUncommittedChanges)
		{
			var pendingJobs = jobs
				.Where(IsPendingCommitAttribution)
				.OrderByDescending(GetSortTimestamp)
				.ToList();

			return new ProjectChangesSelection(
				ProjectChangesSourceType.WorkingDirectory,
				CommitHash: null,
				BaseCommit: null,
				LinkedJobs: pendingJobs);
		}

		var latestCommittedJob = jobs
			.Where(IsCommitted)
			.OrderByDescending(GetSortTimestamp)
			.FirstOrDefault();

		if (latestCommittedJob == null)
		{
			return new ProjectChangesSelection(
				ProjectChangesSourceType.None,
				CommitHash: null,
				BaseCommit: null,
				LinkedJobs: []);
		}

		var linkedJobs = jobs
			.Where(job => IsCommitted(job) && string.Equals(job.GitCommitHash, latestCommittedJob.GitCommitHash, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(GetSortTimestamp)
			.ToList();

		var baseCommit = linkedJobs
			.Select(job => job.GitCommitBefore)
			.FirstOrDefault(commit => !string.IsNullOrWhiteSpace(commit));

		return new ProjectChangesSelection(
			ProjectChangesSourceType.Commit,
			latestCommittedJob.GitCommitHash,
			baseCommit,
			linkedJobs);
	}

	private static bool IsPendingCommitAttribution(Job job)
		=> job.Status == JobStatus.Completed && string.IsNullOrWhiteSpace(job.GitCommitHash);

	private static bool IsCommitted(Job job)
		=> job.Status == JobStatus.Completed && !string.IsNullOrWhiteSpace(job.GitCommitHash);

	private static DateTime GetSortTimestamp(Job job)
		=> job.CompletedAt ?? job.StartedAt ?? job.CreatedAt;
}
