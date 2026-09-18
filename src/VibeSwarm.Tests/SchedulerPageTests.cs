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
			Assert.Contains("justify-content-between gap-2 gap-sm-3 mb-3 mb-lg-4", html);
			Assert.Contains("Repo", html);
			Assert.Contains("Copilot", html);
			Assert.Contains($"Next: {nextRunAtUtc.FormatRelativeToNow()}", html);
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
		Assert.Contains("A schedule queues jobs or generates fresh ideas", html);
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
		services.AddSingleton<IAgentService>(new FakeAgentService());
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

	private sealed class FakeProjectService : FakeProjectServiceBase
	{
		public override Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public override Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public override Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public override Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public override Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public override Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
		public override Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeAgentService : FakeAgentServiceBase
	{
		public override Task<IEnumerable<Agent>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Agent>>([]);
		public override Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Agent>>([]);
		public override Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Agent?>(null);
		public override Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
	}

	private sealed class FakeProviderService : FakeProviderServiceBase
	{
		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([]);
		public override Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public override Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(null);
		public Task<ProviderModel> AddModelAsync(Guid providerId, ProviderModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProviderModel> UpdateModelAsync(Guid providerId, ProviderModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteModelAsync(Guid providerId, Guid modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Provider?> DetectProviderAsync(Provider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeInferenceProviderService : FakeInferenceProviderServiceBase
	{
		public override Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([]);
		public override Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<InferenceProvider?>(null);
		public override Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([]);
		public override Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>([]);
		public override Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => Task.FromResult<InferenceModel?>(null);
	}

	private sealed class FakeJobService : FakeJobServiceBase
	{
		public override Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Job>>([]);
		public override Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobSummary>>([]);
		public override Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public override Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JobChangeSet>());
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
