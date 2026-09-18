using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectGitNotificationTests
{
	[Fact]
	public void ProjectDetailHeaderCompact_DoesNotRenderInlineAlert_ForGitProgress()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProjectDetailHeaderCompact>(parameters => parameters
			.Add(p => p.Name, "Demo Project")
			.Add(p => p.WorkingPath, "/repo")
			.Add(p => p.IsGitRepository, true)
			.Add(p => p.IsGitOperationInProgress, true)
			.Add(p => p.GitProgressMessage, "Syncing with origin..."));

		Assert.Empty(cut.FindAll(".alert"));
		Assert.Contains("Options", cut.Markup);
	}

	[Fact]
	public void ProjectChangesTab_CommitAndPush_ReportsSuccessForToastCallback()
	{
		using var context = new BunitContext();
		var versionControlService = new RecordingVersionControlService();
		var jobService = new RecordingJobService();
		string? gitOperationMessage = null;
		var changesCommittedCount = 0;
		var jobId = Guid.NewGuid();

		context.Services.AddSingleton<IVersionControlService>(versionControlService);
		context.Services.AddSingleton<IJobService>(jobService);

		var cut = context.Render<ProjectChangesTab>(parameters => parameters
			.Add(p => p.WorkingPath, "/repo")
			.Add(p => p.Jobs, new List<Job>
			{
				new()
				{
					Id = jobId,
					Status = JobStatus.Completed,
					Title = "Implement toast notifications"
				}
			})
			.Add(p => p.IsGitRepository, true)
			.Add(p => p.HasUncommittedChanges, true)
			.Add(p => p.UncommittedFilesCount, 1)
			.Add(p => p.WorkingTreeStatus, new GitWorkingTreeStatus
			{
				HasUncommittedChanges = true,
				ChangedFilesCount = 1,
				ChangedFiles = ["README.md"]
			})
			.Add(p => p.OnGitOperationCompleted, (string? message) => gitOperationMessage = message)
			.Add(p => p.OnChangesCommitted, () => changesCommittedCount++));

		cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#changesTabCommitMessage")));

		cut.Find("#changesTabCommitMessage").Input("Update flash alerts to toast notifications");
		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Commit & Push", StringComparison.Ordinal))
			.Click();

		cut.WaitForAssertion(() =>
		{
			Assert.Equal("Successfully committed and pushed changes", gitOperationMessage);
			Assert.Equal(1, changesCommittedCount);
			Assert.Contains((jobId, "abcdef1"), jobService.GitCommitHashUpdates);
			Assert.Empty(cut.FindAll(".alert-danger"));
		});
	}

	[Fact]
	public void ProjectChangesTab_CommitAndPush_WhenPushFails_StaysInlineForRetry()
	{
		using var context = new BunitContext();
		var versionControlService = new RecordingVersionControlService
		{
			PushResult = new GitOperationResult
			{
				Success = false,
				Error = "remote rejected"
			}
		};
		var jobService = new RecordingJobService();
		string? gitOperationMessage = null;
		var changesCommittedCount = 0;
		var jobId = Guid.NewGuid();

		context.Services.AddSingleton<IVersionControlService>(versionControlService);
		context.Services.AddSingleton<IJobService>(jobService);

		var cut = context.Render<ProjectChangesTab>(parameters => parameters
			.Add(p => p.WorkingPath, "/repo")
			.Add(p => p.Jobs, new List<Job>
			{
				new()
				{
					Id = jobId,
					Status = JobStatus.Completed,
					Title = "Implement toast notifications"
				}
			})
			.Add(p => p.IsGitRepository, true)
			.Add(p => p.HasUncommittedChanges, true)
			.Add(p => p.UncommittedFilesCount, 1)
			.Add(p => p.WorkingTreeStatus, new GitWorkingTreeStatus
			{
				HasUncommittedChanges = true,
				ChangedFilesCount = 1,
				ChangedFiles = ["README.md"]
			})
			.Add(p => p.OnGitOperationCompleted, (string? message) => gitOperationMessage = message)
			.Add(p => p.OnChangesCommitted, () => changesCommittedCount++));

		cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#changesTabCommitMessage")));

		cut.Find("#changesTabCommitMessage").Input("Update flash alerts to toast notifications");
		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Commit & Push", StringComparison.Ordinal))
			.Click();

		cut.WaitForAssertion(() =>
		{
			Assert.Null(gitOperationMessage);
			Assert.Equal(1, changesCommittedCount);
			Assert.Contains((jobId, "abcdef1"), jobService.GitCommitHashUpdates);
			Assert.Contains("Commit succeeded but push failed: remote rejected", cut.Markup);
		});
	}

	private sealed class RecordingVersionControlService : FakeVersionControlServiceBase
	{
		private const string Diff = """
			diff --git a/README.md b/README.md
			index 1111111..2222222 100644
			--- a/README.md
			+++ b/README.md
			@@ -1 +1 @@
			-old
			+new
			""";

		public GitOperationResult PushResult { get; set; } = new()
		{
			Success = true
		};

		public override Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public override Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(true);
		public override Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>("abcdef1");
		public override Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>("main");
		public override Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(true);
		public override Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus { HasUncommittedChanges = true, ChangedFilesCount = 1, ChangedFiles = ["README.md"] });
		public override Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["README.md"]);
		public override Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Diff);
		public override Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Diff);
		public override Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public override Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => Task.FromResult(new GitOperationResult { Success = true, CommitHash = "abcdef1" });
		public override Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => Task.FromResult(PushResult);
		public override Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);
		public override string? ExtractGitHubRepository(string? remoteUrl) => null;
	}

	private sealed class RecordingJobService : FakeJobServiceBase
	{
		public List<(Guid JobId, string CommitHash)> GitCommitHashUpdates { get; } = [];

		public override Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default)
		{
			GitCommitHashUpdates.Add((id, commitHash));
			return Task.FromResult(true);
		}

		public override Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JobChangeSet>());
	}
}
