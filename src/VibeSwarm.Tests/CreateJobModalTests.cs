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
