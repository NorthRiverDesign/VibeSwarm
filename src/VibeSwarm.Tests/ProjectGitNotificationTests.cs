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

	private sealed class RecordingVersionControlService : IVersionControlService
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

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>("abcdef1");
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>("main");
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus { HasUncommittedChanges = true, ChangedFilesCount = 1, ChangedFiles = ["README.md"] });
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["README.md"]);
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Diff);
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Diff);
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => Task.FromResult(new GitOperationResult { Success = true, CommitHash = "abcdef1" });
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => Task.FromResult(PushResult);
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null) => throw new NotSupportedException();
		public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);
		public Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => throw new NotSupportedException();
		public string? ExtractGitHubRepository(string? remoteUrl) => null;
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> InitializeRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => throw new NotSupportedException();
		public Task<GitOperationResult> AddRemoteAsync(string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class RecordingJobService : IJobService
	{
		public List<(Guid JobId, string CommitHash)> GitCommitHashUpdates { get; } = [];

		public Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default)
		{
			GitCommitHashUpdates.Add((id, commitHash));
			return Task.FromResult(true);
		}

		public Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ForceCancelAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task UpdateProgressAsync(Guid id, string? currentActivity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResetJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitDeliveryAsync(Guid id, string? commitHash = null, int? pullRequestNumber = null, string? pullRequestUrl = null, DateTime? pullRequestCreatedAt = null, DateTime? mergedAt = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType, string? choices = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, string? reasoningEffort = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> RetrySelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> PrioritizeSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JobChangeSet>());
	}
}
