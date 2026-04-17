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
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([]));
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

		Assert.Contains("Security Reviewer", cut.Markup);
		Assert.Contains("GitHub Copilot", cut.Markup);
		Assert.DoesNotContain("No enabled agents", cut.Markup);
	}

	[Fact]
	public void JobScheduleModal_AgentDropdown_UsesSelectedProjectDetails_WhenBulkProjectListIsStale()
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
			IsEnabled = true
		};
		var secondAgent = new Agent
		{
			Id = secondAgentId,
			Name = "Release Manager",
			IsEnabled = true
		};
		var bulkProject = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			AgentAssignments =
			[
				new ProjectAgent
				{
					ProjectId = projectId,
					AgentId = firstAgentId,
					Agent = firstAgent,
					ProviderId = providerId,
					Provider = provider,
					IsEnabled = true
				}
			]
		};
		var detailedProject = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			AgentAssignments =
			[
				new ProjectAgent
				{
					ProjectId = projectId,
					AgentId = firstAgentId,
					Agent = firstAgent,
					ProviderId = providerId,
					Provider = provider,
					IsEnabled = true
				},
				new ProjectAgent
				{
					ProjectId = projectId,
					AgentId = secondAgentId,
					Agent = secondAgent,
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
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([bulkProject], detailedProject));
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
		Assert.Contains("Release Manager", cut.Markup);
	}

	[Fact]
	public void JobScheduleModal_AgentDropdown_MergesBulkAssignments_WhenDetailedProjectIsMissingNewAgent()
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
			Name = "Front-End Developer",
			IsEnabled = true
		};
		var secondAgent = new Agent
		{
			Id = secondAgentId,
			Name = "Code Reviewer",
			IsEnabled = true
		};
		var bulkProject = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			AgentAssignments =
			[
				new ProjectAgent
				{
					ProjectId = projectId,
					AgentId = firstAgentId,
					Agent = firstAgent,
					ProviderId = providerId,
					Provider = provider,
					IsEnabled = true
				},
				new ProjectAgent
				{
					ProjectId = projectId,
					AgentId = secondAgentId,
					Agent = secondAgent,
					ProviderId = providerId,
					Provider = provider,
					IsEnabled = true
				}
			]
		};
		var detailedProject = new Project
		{
			Id = projectId,
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			AgentAssignments =
			[
				new ProjectAgent
				{
					ProjectId = projectId,
					AgentId = firstAgentId,
					Agent = firstAgent,
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
			AgentId = firstAgentId,
			ScheduleType = JobScheduleType.RunJob,
			Prompt = "Review UI",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			IsEnabled = true
		};

		using var context = new BunitContext();
		context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
		context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([bulkProject], detailedProject));
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

		Assert.Contains("Front-End Developer", cut.Markup);
		Assert.Contains("Code Reviewer", cut.Markup);
	}

	/// <summary>
	/// Regression: a newly-added agent assignment whose provider is not yet in the
	/// client-side provider cache must still appear in the dropdown. The old filter
	/// used _providers.Any(p => p.Id == assignment.ProviderId) which would silently
	/// exclude the assignment when the 60-second provider cache was stale. The fix
	/// checks assignment.Provider?.IsEnabled instead, using the nav prop that is
	/// always populated by the API response.
	/// </summary>
	[Fact]
	public void JobScheduleModal_AgentDropdown_ShowsAssignment_WhenProviderNotInClientCache()
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
			Name = "Release Manager",
			IsEnabled = true
		};

		// Simulate the project returned by GetByIdAsync: the new agent assignment has
		// the Provider nav prop populated (as it would be from the server), but the
		// FakeProviderService returns an EMPTY list to simulate a stale provider cache.
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
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([agent]));
		// Empty provider list simulates a stale client-side cache that does not yet
		// include the newly added provider used by the new agent assignment.
		context.Services.AddSingleton<IProviderService>(new FakeProviderService([]));
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
	}

	/// <summary>
	/// Regression: an agent assignment whose provider has IsEnabled=false must still appear in
	/// the scheduler modal agent dropdown. Previously the filter excluded such assignments,
	/// silently hiding agents added after a provider was disabled. The server validates
	/// provider state at save time; the dropdown should not pre-filter by provider status.
	/// </summary>
	[Fact]
	public void JobScheduleModal_AgentDropdown_ShowsAssignment_WhenProviderIsDisabled()
	{
		var agentId = Guid.NewGuid();
		var providerId = Guid.NewGuid();
		var projectId = Guid.NewGuid();

		// Provider is disabled — the old filter (`assignment.Provider?.IsEnabled != false`) would
		// have silently excluded this assignment, causing the agent to vanish from the dropdown.
		var provider = new Provider
		{
			Id = providerId,
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = false,
			IsDefault = false
		};
		var agent = new Agent
		{
			Id = agentId,
			Name = "Security Auditor",
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
			Prompt = "Run security audit",
			Frequency = JobScheduleFrequency.Daily,
			HourUtc = 9,
			IsEnabled = true
		};

		using var context = new BunitContext();
		context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
		context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([project]));
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([agent]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService([]));
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
