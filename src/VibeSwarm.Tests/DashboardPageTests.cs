using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class DashboardPageTests
{
	[Fact]
	public async Task Dashboard_HidesDisabledProviders_AndShowsProjectSortOptions()
	{
		var nowUtc = DateTime.UtcNow;
		var activeProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
		var disabledProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = false
		};
		var dashboardProjects = new[]
		{
			CreateProjectInfo("Alpha", nowUtc.AddHours(-12)),
			CreateProjectInfo("Gamma"),
			CreateProjectInfo("Beta", nowUtc.AddHours(-1))
		};

		var html = await RenderDashboardPageAsync(
			new FakeProjectService(dashboardProjects),
			new FakeProviderService([activeProvider, disabledProvider]));

		Assert.Contains("Sort by", html);
		Assert.Contains("Last ran", html);
		Assert.Contains("Name", html);
		Assert.Contains("Claude", html);
		Assert.DoesNotContain("Copilot", html);
		Assert.Contains("row g-3", html);
		Assert.Contains("row row-cols-1 row-cols-md-2 row-cols-xl-3 g-2 g-lg-3", html);
		Assert.True(html.IndexOf("Beta", StringComparison.Ordinal) < html.IndexOf("Alpha", StringComparison.Ordinal));
		Assert.True(html.IndexOf("Alpha", StringComparison.Ordinal) < html.IndexOf("Gamma", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Dashboard_ShowsRunningJobsSection_WhenProjectsHaveRunningJobs()
	{
		var nowUtc = DateTime.UtcNow;
		var dashboardProjects = new[]
		{
			CreateProjectInfo("Alpha", nowUtc.AddHours(-4)),
			CreateProjectInfo("Beta", nowUtc.AddHours(-2))
		};
		var runningJobs = new[]
		{
			CreateRunningJobInfo("Beta", "Beta active job", JobStatus.Processing, nowUtc.AddMinutes(-10), "Updating files"),
			CreateRunningJobInfo("Alpha", "Alpha active job", JobStatus.Started, nowUtc.AddMinutes(-25))
		};

		var html = await RenderDashboardPageAsync(
			new FakeProjectService(dashboardProjects, runningJobs),
			new FakeProviderService([]));

		Assert.Contains("Running Jobs", html);
		Assert.Contains("Beta active job", html);
		Assert.Contains("Alpha active job", html);
		Assert.Contains("Updating files", html);
		Assert.True(html.IndexOf("Beta active job", StringComparison.Ordinal) < html.IndexOf("Alpha active job", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Dashboard_ShowsRunningJobsBeforeIdeasAndAnalytics_WhenPresent()
	{
		var nowUtc = DateTime.UtcNow;

		var html = await RenderDashboardPageAsync(
			new FakeProjectService(
				[CreateProjectInfo("Alpha", nowUtc.AddHours(-1))],
				[CreateRunningJobInfo("Alpha", "Alpha active job", JobStatus.Processing, nowUtc.AddMinutes(-5))]),
			new FakeProviderService([]),
			new FakeIdeaService(new GlobalIdeasProcessingStatus
			{
				TotalUnprocessedIdeas = 2,
				ProjectsCurrentlyProcessing = 1,
				Projects =
				[
					new ProjectIdeasSummary
					{
						ProjectId = Guid.NewGuid(),
						ProjectName = "Alpha",
						UnprocessedIdeas = 2,
						IsProcessing = true
					}
				]
			}));

		var runningJobsIndex = html.IndexOf("Running Jobs", StringComparison.Ordinal);
		var ideasProcessingIndex = html.IndexOf("Ideas Processing", StringComparison.Ordinal);
		var jobAnalyticsIndex = html.IndexOf("Job Analytics", StringComparison.Ordinal);

		Assert.True(runningJobsIndex >= 0);
		Assert.True(ideasProcessingIndex > runningJobsIndex);
		Assert.True(jobAnalyticsIndex > ideasProcessingIndex);
	}

	[Fact]
	public async Task Dashboard_HidesRunningJobsSection_WhenNoProjectsHaveRunningJobs()
	{
		var dashboardProjects = new[]
		{
			CreateProjectInfo("Alpha", DateTime.UtcNow.AddHours(-1))
		};

		var html = await RenderDashboardPageAsync(
			new FakeProjectService(dashboardProjects, []),
			new FakeProviderService([]));

		Assert.DoesNotContain("Running Jobs", html);
	}

	[Fact]
	public async Task Dashboard_ShowsStopWithoutStartAll_WhenIdeasProcessingIsActive()
	{
		var html = await RenderDashboardPageAsync(
			new FakeProjectService([CreateProjectInfo("Alpha", DateTime.UtcNow.AddHours(-1))]),
			new FakeProviderService([]),
			new FakeIdeaService(new GlobalIdeasProcessingStatus
			{
				TotalUnprocessedIdeas = 7,
				ProjectsCurrentlyProcessing = 2,
				Projects =
				[
					new ProjectIdeasSummary
					{
						ProjectId = Guid.NewGuid(),
						ProjectName = "Alpha",
						UnprocessedIdeas = 3,
						IsProcessing = true
					},
					new ProjectIdeasSummary
					{
						ProjectId = Guid.NewGuid(),
						ProjectName = "Beta",
						UnprocessedIdeas = 4,
						IsProcessing = false
					}
				]
			}));

		Assert.Contains("Ideas Processing", html);
		Assert.Contains("2 projects processing", html);
		Assert.Contains(">Stop<", html);
		Assert.DoesNotContain("Start All Ideas", html);
		Assert.Contains("d-flex flex-column flex-sm-row align-items-stretch align-items-sm-center gap-2 align-self-stretch align-self-sm-auto", html);
		Assert.Contains("d-flex flex-column flex-sm-row align-items-stretch align-items-sm-center gap-2", html);
		Assert.Contains("badge bg-success-subtle text-success-emphasis d-inline-flex align-items-center justify-content-center justify-content-sm-start gap-1 text-wrap align-self-start align-self-sm-auto", html);
	}

	[Fact]
	public async Task Dashboard_ShowsStartAllWithoutStop_WhenIdeasRemainQueued()
	{
		var html = await RenderDashboardPageAsync(
			new FakeProjectService([CreateProjectInfo("Alpha", DateTime.UtcNow.AddHours(-1))]),
			new FakeProviderService([]),
			new FakeIdeaService(new GlobalIdeasProcessingStatus
			{
				TotalUnprocessedIdeas = 4,
				ProjectsCurrentlyProcessing = 0,
				Projects =
				[
					new ProjectIdeasSummary
					{
						ProjectId = Guid.NewGuid(),
						ProjectName = "Alpha",
						UnprocessedIdeas = 4,
						IsProcessing = false
					}
				]
			}));

		Assert.Contains("Start All Ideas", html);
		Assert.DoesNotContain(">Stop<", html);
	}

	private static async Task<string> RenderDashboardPageAsync(
		IProjectService projectService,
		IProviderService providerService,
		IIdeaService? ideaService = null)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton(providerService);
		services.AddSingleton(projectService);
		services.AddSingleton<IVersionControlService>(new FakeVersionControlService());
		services.AddSingleton(ideaService ?? new FakeIdeaService());
		services.AddSingleton<NavigationManager>(new TestNavigationManager());
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
		services.AddSingleton(new HttpProviderService(new HttpClient(new StaticJsonHandler())
		{
			BaseAddress = new Uri("http://localhost")
		}));

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		return await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<VibeSwarm.Client.Pages.Index>();
			return output.ToHtmlString();
		});
	}

	private static DashboardProjectInfo CreateProjectInfo(string name, DateTime? latestJobCreatedAt = null)
	{
		var projectId = Guid.NewGuid();

		return new DashboardProjectInfo
		{
			Project = new Project
			{
				Id = projectId,
				Name = name,
				WorkingPath = $"/tmp/{name.ToLowerInvariant()}",
				CreatedAt = DateTime.UtcNow.AddDays(-7)
			},
			LatestJob = latestJobCreatedAt.HasValue
				? new JobSummary
				{
					Id = Guid.NewGuid(),
					ProjectId = projectId,
					ProviderId = Guid.NewGuid(),
					GoalPrompt = $"{name} latest job",
					Status = JobStatus.Completed,
					CreatedAt = latestJobCreatedAt.Value
				}
				: null
		};
	}

	private static DashboardRunningJobInfo CreateRunningJobInfo(
		string projectName,
		string title,
		JobStatus status,
		DateTime startedAt,
		string? currentActivity = null)
	{
		var projectId = Guid.NewGuid();

		return new DashboardRunningJobInfo
		{
			Project = new Project
			{
				Id = projectId,
				Name = projectName,
				WorkingPath = $"/tmp/{projectName.ToLowerInvariant()}",
				CreatedAt = DateTime.UtcNow.AddDays(-7)
			},
			Job = new JobSummary
			{
				Id = Guid.NewGuid(),
				ProjectId = projectId,
				ProviderId = Guid.NewGuid(),
				Title = title,
				GoalPrompt = title,
				Status = status,
				CreatedAt = startedAt.AddMinutes(-1),
				StartedAt = startedAt,
				CurrentActivity = currentActivity
			}
		};
	}

	private sealed class FakeProviderService(IReadOnlyList<Provider> providers) : IProviderService
	{
		private readonly IReadOnlyList<Provider> _providers = providers;

		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>(_providers);
		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.Id == id));
		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.IsDefault));
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

	private sealed class FakeProjectService(
		IReadOnlyList<DashboardProjectInfo> dashboardProjects,
		IReadOnlyList<DashboardRunningJobInfo>? runningJobs = null) : IProjectService
	{
		private readonly IReadOnlyList<DashboardProjectInfo> _dashboardProjects = dashboardProjects;
		private readonly IReadOnlyList<DashboardRunningJobInfo> _runningJobs = runningJobs ?? [];

		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>(_dashboardProjects.Select(project => project.Project));
		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>(_dashboardProjects.Select(project => project.Project).Take(count));
		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_dashboardProjects.Select(project => project.Project).FirstOrDefault(project => project.Id == id));
		public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<DashboardProjectInfo>>(_dashboardProjects.Take(count));
		public Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default)
			=> Task.FromResult(new DashboardJobMetrics
			{
				RangeDays = rangeDays,
				Buckets = []
			});
		public Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<DashboardRunningJobInfo>>(_runningJobs);
	}

	private sealed class FakeVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
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
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus());
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

	private sealed class FakeIdeaService(GlobalIdeasProcessingStatus? globalProcessingStatus = null) : IIdeaService
	{
		private readonly GlobalIdeasProcessingStatus _globalProcessingStatus = globalProcessingStatus ?? new GlobalIdeasProcessingStatus();

		public Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Idea>>([]);
		public Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default) => Task.FromResult(new ProjectIdeasListResult());
		public Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult<Job?>(null);
		public Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult<IdeaAttachment?>(null);
		public Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Guid>>([]);
		public Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(_globalProcessingStatus);
		public Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task StopAllProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default) => Task.FromResult(new SuggestIdeasResult());
	}

	private sealed class StaticJsonHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri?.AbsolutePath == "/api/providers/usage-summaries")
			{
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = JsonContent.Create(new Dictionary<Guid, ProviderUsageSummary>())
				});
			}

			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
		}
	}

	private sealed class TestNavigationManager : NavigationManager
	{
		public TestNavigationManager()
		{
			Initialize("http://localhost/", "http://localhost/");
		}

		protected override void NavigateToCore(string uri, bool forceLoad)
		{
			Uri = ToAbsoluteUri(uri).ToString();
		}
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
