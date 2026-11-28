using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Components.Scheduler;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Tests for the scheduler modal agent dropdown. Agents are global entities — they
/// appear in the dropdown based on IsEnabled status, not per-project assignments.
/// </summary>
public sealed class JobScheduleModalTests
{
	[Fact]
	public void JobScheduleModal_AScheduledJob_RunsOnAProfile_WithNoProviderOrModelToPick()
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude Code", Type = ProviderType.Claude, IsEnabled = true, IsDefault = true };
		var nightly = new Profile { Id = Guid.NewGuid(), Name = "Nightly", ProviderId = provider.Id, Provider = provider, ModelId = "sonnet" };
		var project = new Project { Id = Guid.NewGuid(), Name = "VibeSwarm", WorkingPath = "/tmp/vibeswarm" };
		var editSchedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProfileId = nightly.Id,
			ProviderId = provider.Id,
			ModelId = "sonnet",
			ScheduleType = JobScheduleType.RunJob,
			Prompt = "Tidy up",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			IsEnabled = true
		};

		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([project]));
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
		context.Services.AddSingleton<IJobScheduleService>(new FakeJobScheduleService());
		context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(nightly));
		context.Services.AddSingleton<AppTimeZoneService>();
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<JobScheduleModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditSchedule, editSchedule));

		cut.WaitForAssertion(() => Assert.Equal(nightly.Id.ToString(), cut.Find("#schedule-profile").GetAttribute("value")));
		Assert.Equal(["Project's Build profile", "Nightly"], cut.FindAll("#schedule-profile option").Select(option => option.TextContent));
		Assert.Empty(cut.FindAll("#schedule-provider, #schedule-model"));
	}

	private sealed class FakeProfileService(params Profile[] profiles) : IProfileService
	{
		public Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Profile>>(profiles);
		public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));
		public Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class FakeSettingsService : ISettingsService
	{
		public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(new AppSettings { TimeZoneId = "UTC" });
		public Task<AppSettings> UpdateMaxConcurrentJobsAsync(int maxConcurrentJobs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<AppSettings> UpdateTeamAsync(Guid? leadProfileId, Guid? reviewProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
			=> Task.FromResult(settings);
		public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);
	}

	private sealed class FakeProjectService(IReadOnlyList<Project> projects, Project? detailedProject = null) : FakeProjectServiceBase
	{
		public override Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Project>>(projects);

		public override Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public override Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(detailedProject?.Id == id ? detailedProject : projects.FirstOrDefault(project => project.Id == id));
		public override Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
		public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public override Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public override Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
		public override Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

	private sealed class FakeProviderService(IReadOnlyList<Provider> providers) : FakeProviderServiceBase
	{
		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>(providers);
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

	private sealed class FakeJobScheduleService : IJobScheduleService
	{
		public Task<IEnumerable<JobSchedule>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobSchedule>>([]);
		public Task<JobSchedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<JobSchedule?>(null);
		public Task<JobSchedule> CreateAsync(JobSchedule schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobSchedule> UpdateAsync(JobSchedule schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<JobSchedule> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
