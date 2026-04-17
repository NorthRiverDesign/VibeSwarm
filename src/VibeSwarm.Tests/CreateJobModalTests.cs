using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class CreateJobModalTests
{
	[Fact]
	public void CreateJobModal_RendersTemplateLibraryControls()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
		context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([]));
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([]));
		context.Services.AddSingleton<IJobTemplateService>(new FakeJobTemplateService());
		context.Services.AddSingleton<NotificationService>();

		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};
		var template = new JobTemplate
		{
			Id = Guid.NewGuid(),
			Name = "Fix bug",
			GoalPrompt = "Fix the reported defect",
			ProviderId = provider.Id
		};

		var cut = context.Render<CreateJobModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.JobModel, new Job())
			.Add(component => component.Providers, [provider])
			.Add(component => component.AvailableModels, new List<ProviderModel>())
			.Add(component => component.Branches, new List<GitBranchInfo>())
			.Add(component => component.TemplateLibrary, [template]));

		Assert.Contains("Template Library", cut.Markup);
		Assert.Contains("Start from scratch...", cut.Markup);
		Assert.Contains("Fix bug", cut.Markup);
		Assert.Contains("Save as Template", cut.Markup);
	}

	[Fact]
	public void CreateJobModal_SelectingAgentPresetAppliesAssignedExecutionDefaults()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
		context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([]));
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([]));
		context.Services.AddSingleton<IJobTemplateService>(new FakeJobTemplateService());
		context.Services.AddSingleton<NotificationService>();

		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Security Reviewer",
			Description = "Reviews risky auth and secrets changes.",
			Responsibilities = "Inspect security-sensitive code paths before shipping.",
			DefaultCycleMode = CycleMode.Autonomous,
			DefaultCycleSessionMode = CycleSessionMode.ContinueSession,
			DefaultMaxCycles = 4,
			DefaultCycleReviewPrompt = "Review the last cycle and continue only if work remains."
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			AgentAssignments =
			[
				new ProjectAgent
				{
					ProjectId = Guid.NewGuid(),
					AgentId = agent.Id,
					Agent = agent,
					ProviderId = provider.Id,
					Provider = provider,
					PreferredModelId = "gpt-5.4",
					PreferredReasoningEffort = "high",
					IsEnabled = true
				}
			]
		};

		var cut = context.Render<CreateJobModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.JobModel, new Job())
			.Add(component => component.Project, project)
			.Add(component => component.Providers, [provider])
			.Add(component => component.AvailableModels, new List<ProviderModel>())
			.Add(component => component.Branches, new List<GitBranchInfo>())
			.Add(component => component.TemplateLibrary, []));

		cut.Find("#agentPreset").Change(agent.Id.ToString());

		Assert.Contains("Agent Preset", cut.Markup);
		Assert.Contains("Security Reviewer", cut.Markup);
		Assert.Contains("GitHub Copilot", cut.Markup);
		Assert.Contains("gpt-5.4", cut.Markup);
		Assert.Contains("high", cut.Markup);
		Assert.Contains("Autonomous (max 4 cycles)", cut.Markup);
		Assert.Contains("Continue session between cycles", cut.Markup);
		Assert.Contains("Review the last cycle and continue only if work remains.", cut.Markup);
		Assert.Empty(cut.FindAll("#provider"));
	}

	[Fact]
	public void CreateJobModal_RefreshesAgentAssignmentsFromProjectService_WhenParentProjectIsStale()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
		context.JSInterop.SetupVoid("eval", "document.body.classList.remove('vs-modal-open')");
		context.Services.AddSingleton<IJobTemplateService>(new FakeJobTemplateService());
		context.Services.AddSingleton<NotificationService>();

		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Release Manager",
			DefaultCycleMode = CycleMode.FixedCount,
			DefaultMaxCycles = 2,
			IsEnabled = true
		};
		var staleProject = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm"
		};
		var refreshedProject = new Project
		{
			Id = staleProject.Id,
			Name = staleProject.Name,
			WorkingPath = staleProject.WorkingPath,
			AgentAssignments =
			[
				new ProjectAgent
				{
					ProjectId = staleProject.Id,
					AgentId = agent.Id,
					Agent = null,
					ProviderId = provider.Id,
					Provider = null,
					IsEnabled = true
				}
			]
		};

		context.Services.AddSingleton<IProjectService>(new FakeProjectService([staleProject], refreshedProject));
		context.Services.AddSingleton<IAgentService>(new FakeAgentService([agent]));

		var cut = context.Render<CreateJobModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.JobModel, new Job())
			.Add(component => component.Project, staleProject)
			.Add(component => component.Providers, [provider])
			.Add(component => component.AvailableModels, new List<ProviderModel>())
			.Add(component => component.Branches, new List<GitBranchInfo>())
			.Add(component => component.TemplateLibrary, []));

		cut.WaitForAssertion(() =>
		{
			Assert.Contains("Release Manager", cut.Markup);
		});

		cut.Find("#agentPreset").Change(agent.Id.ToString());

		Assert.Contains("Agent Preset", cut.Markup);
		Assert.Contains("Release Manager", cut.Markup);
		Assert.Contains("GitHub Copilot", cut.Markup);
		Assert.Contains("Fixed count (2 cycles)", cut.Markup);
	}

	private sealed class FakeJobTemplateService : IJobTemplateService
	{
		public Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobTemplate>>([]);
		public Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<JobTemplate?>(null);
		public Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default) => Task.FromResult(template);
		public Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default) => Task.FromResult(template);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(new JobTemplate { Id = id, Name = "Template", GoalPrompt = "Prompt" });
	}

	private sealed class FakeProjectService(IReadOnlyList<Project> projects, Project? detailedProject = null) : IProjectService
	{
		public Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Project>>(projects);

		public Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Project>>([]);

		public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(detailedProject?.Id == id ? detailedProject : projects.FirstOrDefault(project => project.Id == id));

		public Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult<Project?>(null);

		public Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Project> CreateProjectAsync(VibeSwarm.Shared.Models.ProjectCreationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default) => throw new NotSupportedException();
	}
}
