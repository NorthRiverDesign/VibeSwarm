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
}
