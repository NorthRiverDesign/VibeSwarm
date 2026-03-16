using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectChangesSectionResolverTests
{
	[Fact]
	public void Resolve_PrefersWorkingDirectoryChanges_WhenUncommittedChangesExist()
	{
		var jobs = new[]
		{
			CreateCompletedJob("Pending commit", completedAt: new DateTime(2026, 3, 14, 1, 0, 0, DateTimeKind.Utc)),
			CreateCompletedJob(
				"Already committed",
				commitBefore: "aaa111",
				commitHash: "bbb222",
				completedAt: new DateTime(2026, 3, 14, 2, 0, 0, DateTimeKind.Utc))
		};

		var selection = ProjectChangesSectionResolver.Resolve(jobs, hasUncommittedChanges: true);

		Assert.Equal(ProjectChangesSourceType.WorkingDirectory, selection.SourceType);
		Assert.Null(selection.CommitHash);
		Assert.Single(selection.LinkedJobs);
		Assert.Equal("Pending commit", selection.LinkedJobs[0].Title);
	}

	[Fact]
	public void Resolve_UsesLatestCommittedChangeSet_WhenWorkingDirectoryIsClean()
	{
		var jobs = new[]
		{
			CreateCompletedJob(
				"Older commit",
				commitBefore: "1111111",
				commitHash: "2222222",
				completedAt: new DateTime(2026, 3, 14, 1, 0, 0, DateTimeKind.Utc)),
			CreateCompletedJob(
				"Latest commit A",
				commitBefore: "3333333",
				commitHash: "4444444",
				completedAt: new DateTime(2026, 3, 14, 2, 0, 0, DateTimeKind.Utc)),
			CreateCompletedJob(
				"Latest commit B",
				commitBefore: "3333333",
				commitHash: "4444444",
				completedAt: new DateTime(2026, 3, 14, 2, 5, 0, DateTimeKind.Utc))
		};

		var selection = ProjectChangesSectionResolver.Resolve(jobs, hasUncommittedChanges: false);

		Assert.Equal(ProjectChangesSourceType.Commit, selection.SourceType);
		Assert.Equal("4444444", selection.CommitHash);
		Assert.Equal("3333333", selection.BaseCommit);
		Assert.Equal(2, selection.LinkedJobs.Count);
		Assert.All(selection.LinkedJobs, job => Assert.Equal("4444444", job.GitCommitHash));
	}

	[Fact]
	public void Resolve_UsesAnyLinkedJobBaseCommit_WhenLatestLinkedJobDoesNotHaveOne()
	{
		var jobs = new[]
		{
			CreateCompletedJob(
				"Linked job without base",
				commitBefore: null,
				commitHash: "abc1234",
				completedAt: new DateTime(2026, 3, 14, 2, 10, 0, DateTimeKind.Utc)),
			CreateCompletedJob(
				"Linked job with base",
				commitBefore: "base999",
				commitHash: "abc1234",
				completedAt: new DateTime(2026, 3, 14, 2, 0, 0, DateTimeKind.Utc))
		};

		var selection = ProjectChangesSectionResolver.Resolve(jobs, hasUncommittedChanges: false);

		Assert.Equal(ProjectChangesSourceType.Commit, selection.SourceType);
		Assert.Equal("abc1234", selection.CommitHash);
		Assert.Equal("base999", selection.BaseCommit);
		Assert.Equal(2, selection.LinkedJobs.Count);
	}

	[Fact]
	public void Resolve_ReturnsNone_WhenProjectHasNoPendingOrCommittedChanges()
	{
		var jobs = new[]
		{
			new Job
			{
				Title = "Still running",
				Status = JobStatus.Processing,
				CreatedAt = new DateTime(2026, 3, 14, 1, 0, 0, DateTimeKind.Utc)
			}
		};

		var selection = ProjectChangesSectionResolver.Resolve(jobs, hasUncommittedChanges: false);

		Assert.Equal(ProjectChangesSourceType.None, selection.SourceType);
		Assert.Empty(selection.LinkedJobs);
		Assert.False(selection.HasChanges);
	}


	[Fact]
	public void Resolve_FallsBackToPersistedJobDiff_WhenWorkingDirectoryIsClean()
	{
		var jobs = new[]
		{
			CreateCompletedJob(
				"Captured diff",
				completedAt: new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc),
				gitDiff: """
					diff --git a/src/App.cs b/src/App.cs
					--- a/src/App.cs
					+++ b/src/App.cs
					@@ -1 +1 @@
					-old
					+new
					""")
		};

		var selection = ProjectChangesSectionResolver.Resolve(jobs, hasUncommittedChanges: false);

		Assert.Equal(ProjectChangesSourceType.PersistedJobDiff, selection.SourceType);
		Assert.Single(selection.LinkedJobs);
		Assert.Equal(jobs[0].GitDiff, selection.PersistedDiff);
	}

	private static Job CreateCompletedJob(string title, string? commitBefore = null, string? commitHash = null, DateTime? completedAt = null, string? gitDiff = null)
	{
		var createdAt = completedAt?.AddMinutes(-10) ?? new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc);

		return new Job
		{
			Title = title,
			GoalPrompt = title,
			Status = JobStatus.Completed,
			GitCommitBefore = commitBefore,
			GitCommitHash = commitHash,
			GitDiff = gitDiff,
			ChangedFilesCount = string.IsNullOrWhiteSpace(gitDiff) ? null : 1,
			CreatedAt = createdAt,
			StartedAt = createdAt.AddMinutes(1),
			CompletedAt = completedAt ?? createdAt.AddMinutes(2)
		};
	}
}
