using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

public sealed class ProjectsPageTests
{
	[Fact]
	public async Task RenderedProjectsPage_GroupsGitActionsUnderProjectOptions()
	{
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Repo Project",
			WorkingPath = "/tmp/repo-project",
			IsActive = true
		};

		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IProjectService>(new FakeProjectService([project]));
		services.AddSingleton<IJobService>(new FakeJobService());
		services.AddSingleton<IVersionControlService>(new FakeVersionControlService());
		services.AddSingleton<IProviderService>(new FakeProviderService());
		services.AddSingleton<ISettingsService>(new FakeSettingsService());
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<Projects>();
			return output.ToHtmlString();
		});

		Assert.Contains("View Details", html);
		Assert.Contains("Git", html);
		Assert.Contains("Resync with Git", html);
		Assert.DoesNotContain("project-danger-menu", html);
	}

	[Fact]
	public async Task RenderedProjectsPage_ShowsFilterTabs_WhenNoProjectsExist()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IProjectService>(new FakeProjectService([]));
		services.AddSingleton<IJobService>(new FakeJobService());
		services.AddSingleton<IVersionControlService>(new FakeVersionControlService());
		services.AddSingleton<IProviderService>(new FakeProviderService());
		services.AddSingleton<ISettingsService>(new FakeSettingsService());
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<Projects>();
			return output.ToHtmlString();
		});

		Assert.Contains("Active", html);
		Assert.Contains("Inactive", html);
		Assert.Contains("No projects yet", html);
	}

	private sealed class FakeProjectService(IReadOnlyList<Project> projects) : IProjectService
	{
		private readonly IReadOnlyList<Project> _projects = projects;

		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>(_projects);
		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>(_projects.Take(count));
		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_projects.FirstOrDefault(project => project.Id == id));
		public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(_projects.FirstOrDefault(project => project.Id == id) is Project project
				? new Project { Id = project.Id, Name = project.Name, WorkingPath = project.WorkingPath, IsActive = project.IsActive, Jobs = [] }
				: null);
		public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<ProjectWithStats>>(_projects.Select(project => new ProjectWithStats
				{
					Project = project,
					Stats = new ProjectJobStats()
				}));
		public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default)
			=> Task.FromResult(new DashboardJobMetrics
			{
				RangeDays = rangeDays,
				Buckets = []
			});
	}

	private sealed class FakeJobService : IJobService
	{
		public Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<IEnumerable<Job>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
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
		public Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateGitDeliveryAsync(Guid id, string? commitHash = null, int? pullRequestNumber = null, string? pullRequestUrl = null, DateTime? pullRequestCreatedAt = null, DateTime? mergedAt = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType, string? choices = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>("abc1234");
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>("main");
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>("git@github.com:owner/repo.git");
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true) => throw new NotSupportedException();
		public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);
		public Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(GitOperationResult.Succeeded(output: "Synced"));
		public Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => throw new NotSupportedException();
		public string? ExtractGitHubRepository(string? remoteUrl) => "owner/repo";
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus());
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
		public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeSettingsService : ISettingsService
	{
		public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
		public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
		public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("/tmp/projects");
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
