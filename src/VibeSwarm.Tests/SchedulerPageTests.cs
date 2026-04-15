using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Tests;

public sealed class SchedulerPageTests
{
	[Fact]
	public async Task RenderedSchedulerPage_ShowsSchedulesAndActions()
	{
		var timeZoneId = DateTimeHelper.ResolveTimeZone("America/New_York").Id;
		var nextRunAtUtc = DateTime.UtcNow.AddHours(2).AddMinutes(15);
		var lastRunAtUtc = DateTime.UtcNow.AddMinutes(-37);
		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			Prompt = "update dependencies, check security issues",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 0,
			IsEnabled = true,
			NextRunAtUtc = nextRunAtUtc,
			LastRunAtUtc = lastRunAtUtc,
			Project = new Project { Id = Guid.NewGuid(), Name = "Repo", WorkingPath = "/tmp/repo" },
			Provider = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, IsEnabled = true }
		};

		try
		{
			var services = BuildServices(new FakeJobScheduleService([schedule]), timeZoneId);
			await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

			var html = await renderer.Dispatcher.InvokeAsync(async () =>
			{
				var output = await renderer.RenderComponentAsync<Scheduler>();
				return output.ToHtmlString();
			});

			Assert.Contains("Scheduler", html);
			Assert.Contains("update dependencies, check security issues", html);
			Assert.Contains("Pause", html);
			Assert.Contains("Edit", html);
			Assert.Contains("Delete", html);
			Assert.Contains("d-flex align-items-center justify-content-between gap-2 gap-sm-3 mb-3 mb-lg-4", html);
			Assert.Contains("Repo", html);
			Assert.Contains("Copilot", html);
			Assert.Contains($"Next {nextRunAtUtc.FormatRelativeToNow()}", html);
			Assert.Contains($"Last {lastRunAtUtc.FormatRelativeToNow()}", html);
			Assert.DoesNotContain(timeZoneId, html);
			Assert.DoesNotContain(nextRunAtUtc.FormatDateTimeWithZone(), html);
		}
		finally
		{
			DateTimeHelper.ConfigureTimeZone(DateTimeHelper.UtcTimeZoneId);
		}
	}

	[Fact]
	public async Task RenderedSchedulerPage_ShowsEmptyStateWhenNoSchedulesExist()
	{
		var services = BuildServices(new FakeJobScheduleService([]), DateTimeHelper.UtcTimeZoneId);
		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var output = await renderer.RenderComponentAsync<Scheduler>();
			return output.ToHtmlString();
		});

		Assert.Contains("No schedules yet", html);
		Assert.Contains("Create a recurring schedule", html);
	}

	[Fact]
	public async Task RenderedSchedulerPage_ShowsTeamMemberSchedules()
	{
		var timeZoneId = DateTimeHelper.ResolveTimeZone("America/New_York").Id;
		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			Prompt = "review for security issues",
			ExecutionTarget = JobScheduleExecutionTarget.Agent,
			AgentId = Guid.NewGuid(),
			Agent = new Agent { Id = Guid.NewGuid(), Name = "Security Reviewer", IsEnabled = true },
			Frequency = JobScheduleFrequency.Weekly,
			WeeklyDay = DayOfWeek.Friday,
			HourUtc = 9,
			MinuteUtc = 30,
			IsEnabled = true,
			NextRunAtUtc = new DateTime(2026, 3, 27, 9, 30, 0, DateTimeKind.Utc),
			Project = new Project { Id = Guid.NewGuid(), Name = "Repo", WorkingPath = "/tmp/repo" }
		};

		try
		{
			var services = BuildServices(new FakeJobScheduleService([schedule]), timeZoneId);
			await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

			var html = await renderer.Dispatcher.InvokeAsync(async () =>
			{
				var output = await renderer.RenderComponentAsync<Scheduler>();
				return output.ToHtmlString();
			});

			Assert.Contains("Security Reviewer", html);
			Assert.DoesNotContain("Unknown agent", html);
			Assert.Contains("review for security issues", html);
		}
		finally
		{
			DateTimeHelper.ConfigureTimeZone(DateTimeHelper.UtcTimeZoneId);
		}
	}

	[Fact]
	public async Task RenderedSchedulerPage_ShowsIdeaGenerationSchedules()
	{
		var timeZoneId = DateTimeHelper.ResolveTimeZone("America/New_York").Id;
		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ScheduleType = JobScheduleType.GenerateIdeas,
			InferenceProviderId = Guid.NewGuid(),
			InferenceProvider = new InferenceProvider
			{
				Id = Guid.NewGuid(),
				Name = "Local Ollama",
				Endpoint = "http://ollama:11434",
				IsEnabled = true
			},
			IdeaCount = 3,
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 0,
			IsEnabled = true,
			NextRunAtUtc = new DateTime(2026, 3, 22, 9, 0, 0, DateTimeKind.Utc),
			Project = new Project { Id = Guid.NewGuid(), Name = "Repo", WorkingPath = "/tmp/repo" }
		};

		try
		{
			var services = BuildServices(new FakeJobScheduleService([schedule]), timeZoneId);
			await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

			var html = await renderer.Dispatcher.InvokeAsync(async () =>
			{
				var output = await renderer.RenderComponentAsync<Scheduler>();
				return output.ToHtmlString();
			});

			Assert.Contains("Idea Generation", html);
			Assert.Contains("Generate 3 ideas", html);
			Assert.Contains("Local Ollama", html);
		}
		finally
		{
			DateTimeHelper.ConfigureTimeZone(DateTimeHelper.UtcTimeZoneId);
		}
	}

	private static ServiceCollection BuildServices(IJobScheduleService jobScheduleService, string timeZoneId)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton(jobScheduleService);
		services.AddSingleton<ISettingsService>(new FakeSettingsService(timeZoneId));
		services.AddSingleton<AppTimeZoneService>();
		services.AddSingleton<IProjectService>(new FakeProjectService());
		services.AddSingleton<IProviderService>(new FakeProviderService());
		services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
		services.AddSingleton<IJobService>(new FakeJobService());
		services.AddSingleton<NotificationService>();
		services.AddSingleton<NavigationManager>(new TestNavigationManager());
		services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
		return services;
	}

	private sealed class FakeSettingsService(string timeZoneId) : ISettingsService
	{
		public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings
		{
			TimeZoneId = timeZoneId
		});

		public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);

		public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
	}

	private sealed class FakeJobScheduleService(IReadOnlyList<JobSchedule> schedules) : IJobScheduleService
	{
		private readonly IReadOnlyList<JobSchedule> _schedules = schedules;

		public Task<IEnumerable<JobSchedule>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<JobSchedule>>(_schedules);

		public Task<JobSchedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(_schedules.FirstOrDefault(schedule => schedule.Id == id));

		public Task<JobSchedule> CreateAsync(JobSchedule schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobSchedule> UpdateAsync(JobSchedule schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobSchedule> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeProjectService : IProjectService
	{
		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> CreateProjectAsync(Shared.Models.ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
		public Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeProviderService : IProviderService
	{
		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([]);
		public Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public Task<Provider> CreateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider> UpdateAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public IProvider? CreateInstance(Provider provider) => throw new NotSupportedException();
		public Task<bool> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProviderModel>>([]);
		public Task<ProviderModel> AddModelAsync(Guid providerId, ProviderModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProviderModel> UpdateModelAsync(Guid providerId, ProviderModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider?> DetectProviderAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

	private sealed class FakeJobService : IJobService
	{
		public Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
	}
}
