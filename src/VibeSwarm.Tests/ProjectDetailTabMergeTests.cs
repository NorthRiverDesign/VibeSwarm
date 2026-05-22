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

	private sealed class FakeProjectService(Project project) : IProjectService
	{
		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([project]);
		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([project]);
		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == project.Id ? project : null);
		public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
		public Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeJobService(IReadOnlyList<JobSummary> jobs) : IJobService
	{
		public Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default)
			=> Task.FromResult(new ProjectJobsListResult
			{
				Items = jobs.ToList(),
				PageNumber = page,
				PageSize = pageSize,
				TotalCount = jobs.Count,
				ActiveCount = jobs.Count(job => job.Status is JobStatus.New or JobStatus.Pending or JobStatus.Started or JobStatus.Planning or JobStatus.Processing or JobStatus.Paused or JobStatus.Stalled),
				CompletedCount = jobs.Count(job => job.Status == JobStatus.Completed)
			});
		public Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobSummary>>([]);
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
		public Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, string? reasoningEffort = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> RetrySelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> PrioritizeSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeIdeaService(IReadOnlyList<Idea> ideas) : IIdeaService
	{
		public Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Idea>>(ideas);
		public Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default)
			=> Task.FromResult(new ProjectIdeasListResult
			{
				Items = ideas.ToList(),
				PageNumber = page,
				PageSize = pageSize,
				TotalCount = ideas.Count,
				UnprocessedCount = ideas.Count(idea => !idea.JobId.HasValue && !idea.IsProcessing)
			});
		public Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(ideas.FirstOrDefault(idea => idea.Id == id));
		public Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CreateAsync(CreateIdeaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IdeaAttachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<GlobalQueueSnapshot> GetGlobalQueueSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopAllProcessingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeProviderService : IProviderService
	{
		private readonly Provider _provider = new()
		{
			Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};

		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([_provider]);
		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(id == _provider.Id ? _provider : null);
		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(_provider);
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

	private sealed class FakeJobTemplateService : IJobTemplateService
	{
		public Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobTemplate>>([]);
		public Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<JobTemplate?>(null);
		public Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeAgentService : IAgentService
	{
		public Task<IEnumerable<Agent>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<Agent>>([]);
		public Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<Agent>>([]);
		public Task<Agent?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Agent?>(null);
		public Task<Agent> CreateAsync(Agent agent, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<Agent> UpdateAsync(Agent agent, CancellationToken ct = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default) => Task.FromResult(false);
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
		public Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => Task.FromResult(new InferenceHealthResult { IsAvailable = false });
		public Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default) => Task.FromResult(new List<DiscoveredModel>());
		public Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default) => throw new NotSupportedException();
	}

	private sealed class FakeInferenceProviderService : IInferenceProviderService
	{
		public Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([]);
		public Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<InferenceProvider?>(null);
		public Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([]);
		public Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>([]);
		public Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default) => throw new NotSupportedException();
		public Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => Task.FromResult<InferenceModel?>(null);
	}

	private sealed class FakeSettingsService : ISettingsService
	{
		public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
		public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
		public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
	}

	private sealed class FakeVersionControlService : IVersionControlService
	{
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitWorkingTreeStatus());
		public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<GitDiffSummary?>(null);
		public Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken cancellationToken = default, GitCommitOptions? commitOptions = null) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> CreatePullRequestAsync(string workingDirectory, string sourceBranch, string targetBranch, string title, string? body = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> PreviewMergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string targetBranch, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default, bool pushAfterMerge = true, IReadOnlyList<MergeConflictResolution>? conflictResolutions = null) => Task.FromResult(new GitOperationResult());
		public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);
		public Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true) => ownerAndRepo;
		public string? ExtractGitHubRepository(string? remoteUrl) => null;
		public Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
		public Task<GitOperationResult> InitializeRepositoryAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<bool> IsGitHubCliAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GitHubRepositoryBrowserResult());
		public Task<GitOperationResult> CreateGitHubRepositoryAsync(string workingDirectory, string repositoryName, string? description = null, bool isPrivate = false, Action<string>? progressCallback = null, CancellationToken cancellationToken = default, string? gitignoreTemplate = null, string? licenseTemplate = null, bool initializeReadme = false) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> AddRemoteAsync(string workingDirectory, string remoteName, string remoteUrl, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
		public Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
		public Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken cancellationToken = default) => Task.FromResult(new GitOperationResult());
	}
}
