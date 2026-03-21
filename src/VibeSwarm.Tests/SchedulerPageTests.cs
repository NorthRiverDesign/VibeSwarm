using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Tests;

public sealed class SchedulerPageTests
{
	[Fact]
	public async Task RenderedSchedulerPage_ShowsSchedulesAndActions()
	{
		var timeZoneId = DateTimeHelper.ResolveTimeZone("America/New_York").Id;
		var schedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			Prompt = "update dependencies, check security issues",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			MinuteUtc = 0,
			IsEnabled = true,
			NextRunAtUtc = new DateTime(2026, 3, 22, 9, 0, 0, DateTimeKind.Utc),
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
			Assert.Contains("Repo", html);
			Assert.Contains("Copilot", html);
			Assert.Contains(timeZoneId, html);
			Assert.Contains(schedule.NextRunAtUtc.FormatDateTimeWithZone(), html);
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
		Assert.Contains("Create a recurring prompt", html);
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
		services.AddSingleton<NotificationService>();
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

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
	}
}
