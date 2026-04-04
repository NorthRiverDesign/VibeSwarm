using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class JobDetailNavigationTests
{
	[Fact]
	public void JobDetail_WhenJobIdChanges_ReloadsTheSelectedJob()
	{
		var firstJobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
		var secondJobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

		var jobService = new FakeJobService(new Dictionary<Guid, Job>
		{
			[firstJobId] = CreateJob(firstJobId, "First job title", "First goal prompt"),
			[secondJobId] = CreateJob(secondJobId, "Second job title", "Second goal prompt")
		});

		using var context = CreateContext(jobService);

		var host = context.Render<JobDetailHost>(parameters => parameters
			.Add(component => component.JobId, firstJobId));

		Assert.Contains("First job title", host.Markup);
		Assert.DoesNotContain("Second job title", host.Markup);

		host.Instance.NavigateTo(secondJobId);
		host.Render();

		Assert.Contains("Second job title", host.Markup);
		Assert.DoesNotContain("First job title", host.Markup);
		Assert.Equal([firstJobId, secondJobId], jobService.LoadedJobIds);
	}

	[Fact]
	public void JobDetail_ShowsOutcomeSummaryForTerminalJobsWithoutSidebarContent()
	{
		var jobId = Guid.Parse("44444444-4444-4444-4444-444444444444");
		var jobService = new FakeJobService(new Dictionary<Guid, Job>
		{
			[jobId] = CreateJob(jobId, "Stalled job title", "Investigate the failure", JobStatus.Stalled)
		});

		using var context = CreateContext(jobService);

		var cut = context.Render<JobDetail>(parameters => parameters
			.Add(component => component.JobId, jobId));

		Assert.Contains("Run stalled before completion", cut.Markup);
		Assert.Contains("Check the transcript before retrying", cut.Markup);
	}

	[Fact]
	public void JobDetail_FailedGitJobs_LinkOutcomeActionsToUncommittedChangesSection()
	{
		var jobId = Guid.Parse("55555555-5555-5555-5555-555555555555");
		var jobService = new FakeJobService(new Dictionary<Guid, Job>
		{
			[jobId] = CreateJob(jobId, "Failed job title", "Recover the changes", JobStatus.Failed, changedFilesCount: 2, workingPath: "/tmp/outcome-project")
		});

		using var context = CreateContext(jobService, new FakeVersionControlService(isGitRepository: true));

		var cut = context.Render<JobDetail>(parameters => parameters
			.Add(component => component.JobId, jobId));

		Assert.Contains("#job-uncommitted-changes-section", cut.Markup);
		Assert.Contains("id=\"job-uncommitted-changes-section\"", cut.Markup);
	}

	private static BunitContext CreateContext(FakeJobService jobService, IVersionControlService? versionControlService = null)
	{
		var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton<IJobService>(jobService);
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService());
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		if (versionControlService != null)
		{
			context.Services.AddSingleton(versionControlService);
		}
		else
		{
			context.Services.AddSingleton<IVersionControlService>(new FakeVersionControlService());
		}
		context.Services.AddSingleton<NotificationService>();
		context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
		context.Services.AddSingleton<NavigationManager>(new TestNavigationManager());
		return context;
	}

	private static Job CreateJob(
		Guid jobId,
		string title,
		string goalPrompt,
		JobStatus status = JobStatus.Processing,
		int? changedFilesCount = null,
		string? workingPath = null)
		=> new()
		{
			Id = jobId,
			ProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
			Title = title,
			GoalPrompt = goalPrompt,
			Status = status,
			ChangedFilesCount = changedFilesCount,
			CreatedAt = DateTime.UtcNow.AddMinutes(-5),
			Messages = [],
			Project = new Project
			{
				Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
				Name = "Navigation test project",
				WorkingPath = workingPath ?? string.Empty,
				IsActive = true
			}
		};

	private sealed class FakeJobService(Dictionary<Guid, Job> jobs) : IJobService
	{
		private readonly Dictionary<Guid, Job> _jobs = jobs;

		public List<Guid> LoadedJobIds { get; } = [];

		public Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobSummary>>([]);
		public Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Job?>(null);
		public Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default)
		{
			LoadedJobIds.Add(id);
			return Task.FromResult(_jobs.TryGetValue(id, out var job) ? CloneJob(job) : null);
		}
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
		public Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
		public Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

		private static Job CloneJob(Job job)
			=> new()
			{
				Id = job.Id,
				ProjectId = job.ProjectId,
				Title = job.Title,
				GoalPrompt = job.GoalPrompt,
				Status = job.Status,
				CreatedAt = job.CreatedAt,
				Messages = job.Messages.Select(message => new JobMessage
				{
					Id = message.Id,
					Role = message.Role,
					Content = message.Content,
					CreatedAt = message.CreatedAt
				}).ToList(),
				Project = job.Project == null
					? null
					: new Project
					{
						Id = job.Project.Id,
						Name = job.Project.Name,
						IsActive = job.Project.IsActive
					}
			};
	}

	private sealed class JobDetailHost : ComponentBase
	{
		private Guid _jobId;

		[Parameter]
		public Guid JobId
		{
			get => _jobId;
			set => _jobId = value;
		}

		public void NavigateTo(Guid jobId)
		{
			_jobId = jobId;
		}

		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			builder.OpenComponent<JobDetail>(0);
			builder.AddAttribute(1, nameof(JobDetail.JobId), _jobId);
			builder.CloseComponent();
		}
	}

	private sealed class FakeIdeaService : IIdeaService
	{
		public Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Idea>>([]);
		public Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default) => Task.FromResult(new ProjectIdeasListResult());
		public Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult<IdeaAttachment?>(null);
		public Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Guid>>([]);
		public Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GlobalIdeasProcessingStatus());
		public Task<GlobalQueueSnapshot> GetGlobalQueueSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GlobalQueueSnapshot());
		public Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopAllProcessingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeProviderService : IProviderService
	{
		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([]);
		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public IProvider? CreateInstance(Provider config) => null;
		public Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProviderModel>>([]);
		public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProviderModel>>([]);
		public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeVersionControlService(bool isGitRepository = false) : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(isGitRepository);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus());
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => throw new NotSupportedException();
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
		public Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => throw new NotSupportedException();
		public Task<GitOperationResult> AddRemoteAsync(string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}

	private sealed class TestNavigationManager : NavigationManager
	{
		public TestNavigationManager()
		{
			Initialize("http://localhost/", "http://localhost/jobs/view");
		}

		protected override void NavigateToCore(string uri, bool forceLoad)
		{
			Uri = ToAbsoluteUri(uri).ToString();
		}
	}
}
