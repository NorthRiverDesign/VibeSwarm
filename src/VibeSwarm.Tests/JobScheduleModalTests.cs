using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Scheduler;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Regression tests for the scheduler modal agent dropdown bug where newly added
/// agents would not appear in the dropdown if their navigation properties were null
/// (e.g. due to JSON serialization cycles). The fix uses FK-based filtering instead.
/// </summary>
public sealed class JobScheduleModalTests
{
	/// <summary>
	/// Regression: an agent assignment whose Agent nav prop is null (as can happen
	/// after JSON deserialization breaks a circular reference) must still appear in
	/// the scheduler modal agent dropdown, because the fix uses AgentId (FK) rather
	/// than the Agent nav prop to determine inclusion.
	/// </summary>
	[Fact]
	public void JobScheduleModal_AgentDropdown_ShowsAssignment_WhenAgentNavPropIsNull()
	{
		var agentId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var projectId = Guid.NewGuid();

		var provider = new Provider
		{
			Id = providerId,
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};

		// Simulate post-deserialization state: AgentId is set but Agent nav prop is null.
		// This was the root cause of the bug — the old filter required Agent != null.
		var project = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			AgentAssignments =
			[
				new ProjectAgent
				{
					ProjectId = projectId,
					AgentId = agentId,
					Agent = null,        // null nav prop — the exact case that was broken
					ProviderId = providerId,
					Provider = null,     // null nav prop — same
					IsEnabled = true
				}
			]
		};

		var editSchedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			ExecutionTarget = JobScheduleExecutionTarget.Agent,
			AgentId = agentId,
			ScheduleType = JobScheduleType.RunJob,
			Prompt = "Run checks",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			IsEnabled = true
		};

		using var context = new BunitContext();
		context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
		context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([project]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService([provider]));
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
		context.Services.AddSingleton<IJobScheduleService>(new FakeJobScheduleService());
		context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
		context.Services.AddSingleton<AppTimeZoneService>();
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<JobScheduleModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditSchedule, editSchedule));

		// The dropdown must exist and must NOT show the empty-state placeholder.
		Assert.DoesNotContain("No enabled agents", cut.Markup);
		// The agent option must have the correct value attribute.
		Assert.Contains(agentId.ToString(), cut.Markup);
	}

	/// <summary>
	/// Verifies that an agent assignment with fully populated nav props (the happy path)
	/// also renders the agent name in the dropdown, including the provider name fallback.
	/// </summary>
	[Fact]
	public void JobScheduleModal_AgentDropdown_ShowsAgentName_WhenNavPropsArePopulated()
	{
		var agentId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var projectId = Guid.NewGuid();

		var provider = new Provider
		{
			Id = providerId,
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var agent = new Agent
		{
			Id = agentId,
			Name = "Security Reviewer",
			IsEnabled = true
		};
		var project = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			AgentAssignments =
			[
				new ProjectAgent
				{
					ProjectId = projectId,
					AgentId = agentId,
					Agent = agent,
					ProviderId = providerId,
					Provider = provider,
					IsEnabled = true
				}
			]
		};

		var editSchedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			ExecutionTarget = JobScheduleExecutionTarget.Agent,
			AgentId = agentId,
			ScheduleType = JobScheduleType.RunJob,
			Prompt = "Run checks",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			IsEnabled = true
		};

		using var context = new BunitContext();
		context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
		context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([project]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService([provider]));
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
		context.Services.AddSingleton<IJobScheduleService>(new FakeJobScheduleService());
		context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
		context.Services.AddSingleton<AppTimeZoneService>();
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<JobScheduleModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditSchedule, editSchedule));

		Assert.Contains("Security Reviewer", cut.Markup);
		Assert.Contains("GitHub Copilot", cut.Markup);
		Assert.DoesNotContain("No enabled agents", cut.Markup);
	}

	private sealed class FakeSettingsService : ISettingsService
	{
		public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(new AppSettings { TimeZoneId = "UTC" });
		public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
			=> Task.FromResult(settings);
		public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);
	}

	private sealed class FakeProjectService(IReadOnlyList<Project> projects) : IProjectService
	{
		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Project>>(projects);

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

	private sealed class FakeProviderService(IReadOnlyList<Provider> providers) : IProviderService
	{
		public Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>(providers);
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
