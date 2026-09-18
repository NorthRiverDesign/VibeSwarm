using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectDetailTabMergeTests
{
	[Fact]
	public void ProjectDetail_RendersUnifiedJobsTabWithManualButtonIdeaComposerAndLists()
	{
		using var context = CreateContext(
			ideas: [new Idea { Id = Guid.NewGuid(), ProjectId = TestProject.Id, Description = "Existing idea" }],
			jobs: [new JobSummary { Id = Guid.NewGuid(), ProjectId = TestProject.Id, Title = "Existing job", GoalPrompt = "Existing job", Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow }]);

		var cut = context.Render<ProjectDetail>(parameters => parameters.Add(component => component.ProjectId, TestProject.Id));

		cut.WaitForAssertion(() =>
		{
			var tabLabels = cut.FindAll("ul.nav-tabs button.nav-link")
				.Select(button => button.TextContent.Trim())
				.ToList();

			Assert.Contains("flex-nowrap flex-sm-wrap overflow-x-auto overflow-y-hidden overscroll-contain", cut.Markup);
			Assert.DoesNotContain(tabLabels, label => label.StartsWith("Ideas", StringComparison.Ordinal));
			Assert.Contains(tabLabels, label => label.StartsWith("Jobs", StringComparison.Ordinal));
			Assert.Equal(4, tabLabels.Count);

			var createJobButtons = cut.FindAll("button")
				.Count(button => button.TextContent.Contains("Create Job", StringComparison.Ordinal));
			Assert.Equal(1, createJobButtons);

			var markup = cut.Markup;
			Assert.Contains("Describe a feature, bug, or improvement to turn into a job", markup);
			Assert.Contains("Existing idea", markup);
			Assert.Contains("Existing job", markup);
			Assert.Empty(cut.FindAll("[aria-label='Pagination']"));

			Assert.True(markup.IndexOf("Create Job", StringComparison.Ordinal) < markup.IndexOf("Describe a feature, bug, or improvement to turn into a job", StringComparison.Ordinal));
			Assert.True(markup.IndexOf("Describe a feature, bug, or improvement to turn into a job", StringComparison.Ordinal) < markup.IndexOf("Existing idea", StringComparison.Ordinal));
			Assert.True(markup.IndexOf("Existing idea", StringComparison.Ordinal) < markup.IndexOf("Existing job", StringComparison.Ordinal));
		});
	}

	[Fact]
	public void ProjectDetail_HidesIdeasEmptyStateWhenUnifiedJobsTabHasNoIdeas()
	{
		using var context = CreateContext(
			ideas: [],
			jobs: [new JobSummary { Id = Guid.NewGuid(), ProjectId = TestProject.Id, Title = "Existing job", GoalPrompt = "Existing job", Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow }]);

		var cut = context.Render<ProjectDetail>(parameters => parameters.Add(component => component.ProjectId, TestProject.Id));

		cut.WaitForAssertion(() =>
		{
			var markup = cut.Markup;
			Assert.Contains("Describe a feature, bug, or improvement to turn into a job", markup);
			Assert.Contains("Existing job", markup);
			Assert.DoesNotContain("No ideas yet", markup);
		});
	}

	[Fact]
	public void ProjectDetail_KeepsJobsEmptyStateInUnifiedJobsTabWhenNoJobsExist()
	{
		using var context = CreateContext(
			ideas: [],
			jobs: []);

		var cut = context.Render<ProjectDetail>(parameters => parameters.Add(component => component.ProjectId, TestProject.Id));

		cut.WaitForAssertion(() =>
		{
			var markup = cut.Markup;
			Assert.Contains("Describe a feature, bug, or improvement to turn into a job", markup);
			Assert.Contains("No jobs yet", markup);
			Assert.DoesNotContain("No ideas yet", markup);
		});
	}

	private static readonly Project TestProject = new()
	{
		Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
		Name = "VibeSwarm",
		WorkingPath = "/tmp/vibeswarm-tests"
	};

	private static BunitContext CreateContext(IReadOnlyList<Idea> ideas, IReadOnlyList<JobSummary> jobs)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProjectService>(new FakeProjectService(TestProject));
		context.Services.AddSingleton<IJobService>(new FakeJobService(jobs));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		context.Services.AddSingleton<IJobTemplateService>(new FakeJobTemplateService());
		context.Services.AddSingleton<IAgentService>(new FakeAgentService());
		context.Services.AddSingleton<IVersionControlService>(new FakeVersionControlService());
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService(ideas));
		context.Services.AddSingleton<IInferenceService>(new FakeInferenceService());
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
		context.Services.AddSingleton<IAutoPilotService>(new FakeAutoPilotService());
		context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
		context.Services.AddSingleton<NotificationService>();
		context.Services.AddSingleton<QueuePanelStateService>();
		return context;
	}

	private sealed class FakeProjectService(Project project) : FakeProjectServiceBase
	{
		public override Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([project]);
		public override Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([project]);
		public override Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == project.Id ? project : null);
		public override Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public override Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public override Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
		public override Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeJobService(IReadOnlyList<JobSummary> jobs) : FakeJobServiceBase
	{
		public override Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default)
			=> Task.FromResult(new ProjectJobsListResult
			{
				Items = jobs.ToList(),
				PageNumber = page,
				PageSize = pageSize,
				TotalCount = jobs.Count,
				ActiveCount = jobs.Count(job => job.Status is JobStatus.New or JobStatus.Pending or JobStatus.Started or JobStatus.Planning or JobStatus.Processing or JobStatus.Paused or JobStatus.Stalled),
				CompletedCount = jobs.Count(job => job.Status == JobStatus.Completed)
			});
		public override Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobSummary>>([]);
		public Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public override Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
	}

	private sealed class FakeIdeaService(IReadOnlyList<Idea> ideas) : FakeIdeaServiceBase
	{
		public override Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Idea>>(ideas);
		public override Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default)
			=> Task.FromResult(new ProjectIdeasListResult
			{
				Items = ideas.ToList(),
				PageNumber = page,
				PageSize = pageSize,
				TotalCount = ideas.Count,
				UnprocessedCount = ideas.Count(idea => !idea.JobId.HasValue && !idea.IsProcessing)
			});
		public override Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(ideas.FirstOrDefault(idea => idea.Id == id));
		public override Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
	}

	private sealed class FakeProviderService : FakeProviderServiceBase
	{
		private readonly Provider _provider = new()
		{
			Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};

		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([_provider]);
		public override Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == _provider.Id ? _provider : null);
		public override Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(_provider);
	}

	private sealed class FakeJobTemplateService : IJobTemplateService
	{
		public Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobTemplate>>([]);
		public Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<JobTemplate?>(null);
		public Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeAgentService : FakeAgentServiceBase
	{
		public override Task<IEnumerable<Agent>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<Agent>>([]);
		public override Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<Agent>>([]);
		public override Task<Agent?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Agent?>(null);
		public override Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default) => Task.FromResult(false);
	}

	private sealed class FakeAutoPilotService : IAutoPilotService
	{
		public Task<IterationLoop> StartAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task PauseAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task ResumeAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IterationLoop?> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IterationLoop?>(null);
		public Task<List<IterationLoop>> GetHistoryAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(new List<IterationLoop>());
		public Task<IterationLoop> UpdateConfigAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeInferenceService : IInferenceService
	{
		public Task<InferenceHealthResult> ProbeAsync(InferenceProbeRequest request, CancellationToken ct = default) => Task.FromResult(new InferenceHealthResult { IsAvailable = false });
		public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => Task.FromResult(new InferenceHealthResult { IsAvailable = false });
		public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => Task.FromResult(new List<DiscoveredModel>());
		public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
	}

	private sealed class FakeInferenceProviderService : FakeInferenceProviderServiceBase
	{
		public override Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([]);
		public override Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<InferenceProvider?>(null);
		public override Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([]);
		public override Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>([]);
		public override Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => Task.FromResult<InferenceModel?>(null);
	}

	private sealed class FakeSettingsService : ISettingsService
	{
		public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
		public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
		public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
	}

	private sealed class FakeVersionControlService : FakeVersionControlServiceBase
	{
		public override Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public override Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public override Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public override Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus());
		public override Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public override Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public override Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public override Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null) => Task.FromResult(new GitOperationResult());
		public override Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);
		public override Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => ownerAndRepo;
		public override string? ExtractGitHubRepository(string? remoteUrl) => null;
		public override Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public override Task<GitOperationResult> InitializeRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public override Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public override Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GitHubRepositoryBrowserResult());
		public override Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> AddRemoteAsync(string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
		public override Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public override Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
	}
}
