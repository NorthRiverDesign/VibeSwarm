using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class JobsViewTests
{
	[Fact]
	public void RenderedJobsView_ShowsRunningWaitingQueuedAndRecentInThatOrder()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;

		var project = CreateProject();
		var jobService = new FakeJobService
		{
			CurrentJobs =
			[
				CreateJob(project, "Queued later", JobStatus.New, priority: 0, minutesAgo: 30),
				CreateJob(project, "Running job", JobStatus.Processing, minutesAgo: 20),
				CreateJob(project, "Queued first", JobStatus.New, priority: 5, minutesAgo: 10),
				CreateJob(project, "Waiting job", JobStatus.Paused, minutesAgo: 40)
			],
			FinishedJobs = [CreateJob(project, "Finished job", JobStatus.Completed, minutesAgo: 60)]
		};
		var cut = RenderJobsView(context, jobService, project, projectFilter: null);

		cut.WaitForAssertion(() =>
		{
			var sections = cut.FindAll("section[aria-label]").Select(section => section.GetAttribute("aria-label")).ToList();
			Assert.Equal(["Running", "Needs you", "Up next", "Recent"], sections);

			var running = cut.Find("section[aria-label='Running']");
			Assert.Contains("Running job", running.TextContent);
			Assert.Contains("Stop", running.QuerySelector("button")!.TextContent);

			Assert.Contains("Waiting for your reply", cut.Find("section[aria-label='Needs you']").TextContent);

			var queued = cut.FindAll("section[aria-label='Up next'] .list-group-item").Select(row => row.TextContent).ToList();
			Assert.Contains("Queued first", queued[0]);
			Assert.Contains("Queued later", queued[1]);
			Assert.Contains("Then 2 ideas from VibeSwarm", queued[2]);

			Assert.Contains("Finished job", cut.Find("section[aria-label='Recent']").TextContent);
			Assert.Contains("All projects", cut.Find("ul[aria-label='Project']").TextContent);
		});
		Assert.Equal(["finished", "current"], jobService.StatusRequests.Take(2));
	}

	[Fact]
	public void RenderedJobsView_RecentFilterAsksForFailedJobs()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;

		var project = CreateProject();
		var jobService = new FakeJobService();
		var cut = RenderJobsView(context, jobService, project, project.Id);

		cut.WaitForAssertion(() => Assert.Contains("finished", jobService.StatusRequests));

		cut.Find("section[aria-label='Recent'] select").Change("failed");

		cut.WaitForAssertion(() => Assert.Contains("failed", jobService.StatusRequests));
		Assert.NotNull(cut.Find("button[aria-label='New job']"));
	}

	private static JobSummary CreateJob(Project project, string title, JobStatus status, int priority = 0, int minutesAgo = 0) => new()
	{
		Id = Guid.NewGuid(),
		ProjectId = project.Id,
		ProjectName = project.Name,
		Title = title,
		GoalPrompt = title,
		Status = status,
		Priority = priority,
		CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo)
	};

	private static IRenderedComponent<JobsView> RenderJobsView(BunitContext context, FakeJobService jobService, Project project, Guid? projectFilter)
	{
		context.Services.AddSingleton<IJobService>(jobService);
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([project]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		context.Services.AddSingleton<IJobTemplateService>(new FakeJobTemplateService());
		context.Services.AddSingleton<IVersionControlService>(new FakeVersionControlService());
		context.Services.AddSingleton<NotificationService>();
		context.Services.AddSingleton<IIdeaService>(new FakeIdeaService(project));

		return context.Render<JobsView>(parameters => parameters
			.Add(component => component.ProjectFilter, projectFilter));
	}

	private static Project CreateProject() => new()
	{
		Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
		Name = "VibeSwarm",
		WorkingPath = "/tmp/vibeswarm-tests",
		IsActive = true
	};

	private sealed class FakeJobService : FakeJobServiceBase
	{
		public List<string> StatusRequests { get; } = [];
		public override Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);

		public List<JobSummary> CurrentJobs { get; init; } = [];
		public List<JobSummary> FinishedJobs { get; init; } = [];

		public override Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
		{
			StatusRequests.Add(statusFilter);
			var items = statusFilter == "current" ? CurrentJobs : FinishedJobs;

			return Task.FromResult(new JobsListResult
			{
				PageNumber = page,
				PageSize = pageSize,
				TotalCount = items.Count,
				Items = items,
				ProjectCounts =
				[
					new JobProjectCountSummary
					{
						ProjectId = projectId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
						TotalCount = 3,
						ActiveCount = 1
					}
				]
			});
		}

		public override Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobSummary>>([]);
		public override Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public override Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JobChangeSet>());
	}

	private sealed class FakeIdeaService(Project project) : FakeIdeaServiceBase
	{
		public override Task<GlobalQueueSnapshot> GetGlobalQueueSnapshotAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(new GlobalQueueSnapshot
			{
				UpcomingIdeas =
				[
					new GlobalQueueIdeaSummary { IdeaId = Guid.NewGuid(), ProjectId = project.Id, ProjectName = project.Name, Description = "One", IsProjectProcessing = true },
					new GlobalQueueIdeaSummary { IdeaId = Guid.NewGuid(), ProjectId = project.Id, ProjectName = project.Name, Description = "Two", IsProjectProcessing = true },
					new GlobalQueueIdeaSummary { IdeaId = Guid.NewGuid(), ProjectId = project.Id, ProjectName = project.Name, Description = "Already queued", IsProjectProcessing = true, HasQueuedJob = true }
				]
			});
	}

	private sealed class FakeProjectService(IReadOnlyList<Project> projects) : FakeProjectServiceBase
	{
		public override Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>(projects);
		public override Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public override Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(projects.FirstOrDefault(project => project.Id == id));
		public override Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public override Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public override Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
		public override Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeProviderService : FakeProviderServiceBase
	{
		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([]);
		public override Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public override Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
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
