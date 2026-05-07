using Bunit;
using Microsoft.Extensions.DependencyInjection;
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
	/// <summary>
	/// Enabled agents with a default provider should appear in the scheduler dropdown
	/// regardless of whether they are assigned to the selected project.
	/// </summary>
	[Fact]
	public void JobScheduleModal_AgentDropdown_ShowsGlobalAgents_WithoutProjectAssignment()
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
			Name = "Code Reviewer",
			IsEnabled = true,
			DefaultProviderId = providerId,
			DefaultProvider = provider
		};

		// Project has NO AgentAssignments — agents should still appear in the dropdown.
		var project = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm"
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
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([agent]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService([provider]));
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
		context.Services.AddSingleton<IJobScheduleService>(new FakeJobScheduleService());
		context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
		context.Services.AddSingleton<AppTimeZoneService>();
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<JobScheduleModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditSchedule, editSchedule));

		Assert.DoesNotContain("No enabled agents", cut.Markup);
		Assert.Contains(agentId.ToString(), cut.Markup);
		Assert.Contains("Code Reviewer", cut.Markup);
	}

	/// <summary>
	/// Multiple enabled agents should all appear in the dropdown.
	/// </summary>
	[Fact]
	public void JobScheduleModal_AgentDropdown_ShowsMultipleAgents()
	{
		var firstAgentId = Guid.NewGuid();
		var secondAgentId = Guid.NewGuid();
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

		var firstAgent = new Agent
		{
			Id = firstAgentId,
			Name = "Security Reviewer",
			IsEnabled = true,
			DefaultProviderId = providerId,
			DefaultProvider = provider
		};

		var secondAgent = new Agent
		{
			Id = secondAgentId,
			Name = "Front-End Developer",
			IsEnabled = true,
			DefaultProviderId = providerId,
			DefaultProvider = provider
		};

		var project = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm"
		};

		var editSchedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			ExecutionTarget = JobScheduleExecutionTarget.Agent,
			AgentId = firstAgentId,
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
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([firstAgent, secondAgent]));
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
		Assert.Contains("Front-End Developer", cut.Markup);
		Assert.DoesNotContain("No enabled agents", cut.Markup);
	}

	/// <summary>
	/// When no agents are enabled, the dropdown should show the empty-state message.
	/// </summary>
	[Fact]
	public void JobScheduleModal_AgentDropdown_ShowsEmptyState_WhenNoEnabledAgents()
	{
		var projectId = Guid.NewGuid();

		var project = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm"
		};

		var editSchedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			ExecutionTarget = JobScheduleExecutionTarget.Agent,
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
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService([]));
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
		context.Services.AddSingleton<IJobScheduleService>(new FakeJobScheduleService());
		context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
		context.Services.AddSingleton<AppTimeZoneService>();
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<JobScheduleModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditSchedule, editSchedule));

		Assert.Contains("No enabled agents", cut.Markup);
	}

	/// <summary>
	/// An agent with a default provider should display the provider name in parentheses.
	/// </summary>
	[Fact]
	public void JobScheduleModal_AgentDropdown_DisplaysProviderName()
	{
		var agentId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var projectId = Guid.NewGuid();

		var provider = new Provider
		{
			Id = providerId,
			Name = "Claude Code",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};

		var agent = new Agent
		{
			Id = agentId,
			Name = "Code Reviewer",
			IsEnabled = true,
			DefaultProviderId = providerId,
			DefaultProvider = provider
		};

		var project = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm"
		};

		var editSchedule = new JobSchedule
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			ExecutionTarget = JobScheduleExecutionTarget.Agent,
			AgentId = agentId,
			ScheduleType = JobScheduleType.RunJob,
			Prompt = "Review code",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			IsEnabled = true
		};

		using var context = new BunitContext();
		context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
		context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([project]));
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([agent]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService([provider]));
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService());
		context.Services.AddSingleton<IJobScheduleService>(new FakeJobScheduleService());
		context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
		context.Services.AddSingleton<AppTimeZoneService>();
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<JobScheduleModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditSchedule, editSchedule));

		Assert.Contains("Code Reviewer (Claude Code)", cut.Markup);
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

	private sealed class FakeProjectService(IReadOnlyList<Project> projects, Project? detailedProject = null) : IProjectService
	{
		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Project>>(projects);

		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(detailedProject?.Id == id ? detailedProject : projects.FirstOrDefault(project => project.Id == id));
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

	private sealed class FakeAgentService(IReadOnlyList<Agent> agents) : IAgentService
	{
		public Task<IEnumerable<Agent>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<Agent>>(agents);
		public Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<Agent>>(agents.Where(agent => agent.IsEnabled));
		public Task<Agent?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(agents.FirstOrDefault(agent => agent.Id == id));
		public Task<Agent> CreateAsync(Agent agent, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<Agent> UpdateAsync(Agent agent, CancellationToken ct = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
			=> Task.FromResult(agents.Any(agent =>
				string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase) &&
				(!excludeId.HasValue || agent.Id != excludeId.Value)));
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
