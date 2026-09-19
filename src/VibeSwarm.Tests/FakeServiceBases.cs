using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Tests;

// Base test doubles for the service interfaces that component tests stub out. Every member
// throws, so a derived fake overrides only the calls its test actually exercises and an
// unexpected call fails loudly instead of silently returning a default.

internal abstract class FakeProviderServiceBase : IProviderService
{
	public virtual Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual IProvider? CreateInstance(Provider config) => null;
	public virtual Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProviderModel>>([]);
	public virtual Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<UsageRefreshResult> RefreshUsageAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal abstract class FakeProjectServiceBase : IProjectService
{
	public virtual Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

	// IProjectService supplies a default implementation of this member. A base class that
	// inherits that default would satisfy the interface itself, leaving a derived fake's own
	// method unreachable through the interface - so declare it virtual here and mirror the
	// interface default as the fallback.
	public virtual Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult(new GitHubRepositoryBrowserResult
		{
			ErrorMessage = "GitHub repository browsing is not available."
		});

	public virtual Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal abstract class FakeInferenceProviderServiceBase : IInferenceProviderService
{
	public virtual Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default) => throw new NotSupportedException();
	public virtual Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => throw new NotSupportedException();
}

internal abstract class FakeAgentServiceBase : IAgentService
{
	public virtual Task<IEnumerable<Agent>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Agent> CreateAsync(Agent agent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Agent> UpdateAsync(Agent agent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal abstract class FakeVersionControlServiceBase : IVersionControlService
{
	public virtual Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> CommitAllChangesAsync( string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> CommitAndPushAsync( string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> CreatePullRequestAsync( string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> PreviewMergeBranchAsync( string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> MergeBranchAsync( string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null) => throw new NotSupportedException();
	public virtual Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> HardCheckoutBranchAsync( string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> SyncWithOriginAsync( string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> CloneRepositoryAsync( string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => throw new NotSupportedException();
	public virtual string? ExtractGitHubRepository(string? remoteUrl) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> CreateBranchAsync( string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> DiscardAllChangesAsync( string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> PreserveChangesAsync( string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IReadOnlyList<string>> GetCommitLogAsync( string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> InitializeRepositoryAsync( string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> CreateGitHubRepositoryAsync( string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> AddRemoteAsync( string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IReadOnlyDictionary<string, string>> GetRemotesAsync( string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> CloneWithGitHubCliAsync( string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GitOperationResult> PruneRemoteBranchesAsync( string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();

	// Mirrors the interface's own default implementation - see the note on
	// FakeProjectServiceBase.BrowseGitHubRepositoriesAsync for why it must be declared here.
	public virtual Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult(new GitHubRepositoryBrowserResult
		{
			ErrorMessage = "GitHub repository browsing is not available."
		});
}
internal abstract class FakeJobServiceBase : IJobService
{
	public virtual Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> ForceCancelAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task UpdateProgressAsync(Guid id, string? currentActivity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> ResetJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> UpdateGitDeliveryAsync( Guid id, string? commitHash = null, int? pullRequestNumber = null, string? pullRequestUrl = null, DateTime? pullRequestCreatedAt = null, DateTime? mergedAt = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType, string? choices = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, string? reasoningEffort = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<int> RetrySelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<int> CancelSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<int> PrioritizeSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

	public virtual Task<bool> TryFailOverToNextExecutionTargetAsync(Guid id, string? reason, CancellationToken cancellationToken = default) => Task.FromResult(false);
	public virtual Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal abstract class FakeIdeaServiceBase : IIdeaService
{
	public virtual Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<GlobalQueueSnapshot> GetGlobalQueueSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task StopAllProcessingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public virtual Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
