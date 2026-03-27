using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Ideas;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Tests;

public sealed class IdeasPanelTests
{
	[Fact]
	public async Task RenderedIdeasPanel_ShowsCompactStatusAndActionHints()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IProjectService>(new FakeProjectService());
		services.AddSingleton<IIdeaService>(new FakeIdeaService());
		services.AddSingleton<IJobService>(new FakeJobService());
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			Description = "Refresh the project details layout",
			ProjectId = Guid.NewGuid()
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(IdeasPanel.Ideas)] = new List<Idea> { idea },
				[nameof(IdeasPanel.TotalIdeasCount)] = 7,
				[nameof(IdeasPanel.UnprocessedIdeasCount)] = 3,
				[nameof(IdeasPanel.IsPageLoading)] = true,
				[nameof(IdeasPanel.HasDefaultProvider)] = false,
				[nameof(IdeasPanel.HasInference)] = true,
				[nameof(IdeasPanel.AvailableInferenceProviders)] = new List<InferenceProvider>(),
				[nameof(IdeasPanel.AvailableProviders)] = new List<Provider>()
			});
			var output = await renderer.RenderComponentAsync<IdeasPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Refreshing ideas", html);
		Assert.Contains("3 pending", html);
		Assert.Contains("Set default provider", html);
		Assert.Contains("Add idea", html);
		Assert.Contains("Set a default provider to enable idea processing", html);
		Assert.DoesNotContain("class=\"badge bg-secondary\">7</span>", html);
		Assert.DoesNotContain("Short description of a feature or update.", html);
		Assert.DoesNotContain("card-header", html);
		Assert.Contains($"maxlength=\"{ValidationLimits.IdeaDescriptionMaxLength}\"", html);
		Assert.Contains($"0/{ValidationLimits.IdeaDescriptionMaxLength} characters", html);
		Assert.Contains("border rounded-3", html);
		Assert.DoesNotContain("border rounded-3 overflow-hidden", html);
	}

	[Fact]
	public async Task RenderedIdeasPanel_ShowsSuggestButton_WhenConfiguredProvidersExistWithoutInference()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<IProjectService>(new FakeProjectService());
		services.AddSingleton<IIdeaService>(new FakeIdeaService());
		services.AddSingleton<IJobService>(new FakeJobService());
		services.AddSingleton<NotificationService>();
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(IdeasPanel.HasInference)] = false,
				[nameof(IdeasPanel.AvailableInferenceProviders)] = new List<InferenceProvider>(),
				[nameof(IdeasPanel.AvailableProviders)] =
					new List<Provider> { new() { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = true } }
			});
			var output = await renderer.RenderComponentAsync<IdeasPanel>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Suggest", html);
	}

	[Fact]
	public async Task IdeaListItem_UsesComfortableMobileSpacingAndJustifiedActions()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var idea = new Idea
		{
			Id = Guid.NewGuid(),
			Description = "Tighten mobile spacing for idea actions",
			ProjectId = Guid.NewGuid()
		};

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(IdeaListItem.Idea)] = idea,
				[nameof(IdeaListItem.HasDefaultProvider)] = true
			});
			var output = await renderer.RenderComponentAsync<IdeaListItem>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("list-group-item p-3", html);
		Assert.Contains("justify-content-between align-items-stretch align-items-sm-start gap-3", html);
		Assert.Contains("align-self-stretch align-self-sm-start gap-2", html);
	}

	private sealed class FakeProjectService : IProjectService
	{
		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default)
			=> Task.FromResult(new DashboardJobMetrics
			{
				RangeDays = rangeDays,
				Buckets = []
			});
		public Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeIdeaService : IIdeaService
	{
		public Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Idea>>([]);
		public Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default)
			=> Task.FromResult(new ProjectIdeasListResult());
		public Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task<Job?> ConvertToJobAsync(Guid ideaId, CancellationToken cancellationToken = default) => Task.FromResult<Job?>(null);
		public Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<Idea?>(null);
		public Task StartProcessingAsync(Guid projectId, bool autoCommit = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
		public Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default)
			=> Task.FromResult(new SuggestIdeasResult());
	}

	private sealed class FakeJobService : IJobService
	{
		public Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobSummary>>([]);
		public Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Job?>(null);
		public Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
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
		public Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}
}
