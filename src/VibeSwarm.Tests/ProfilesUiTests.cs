using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using VibeSwarm.Client.Components.Agents;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Components.Profiles;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Models;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProfilesUiTests
{
	private static readonly Provider Claude = new() { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
	private static readonly Provider OpenCode = new() { Id = Guid.NewGuid(), Name = "OpenCode", Type = ProviderType.OpenCode, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };

	[Fact]
	public void ProfilesPage_ListsEachProfileWithWhatItRunsOn()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(
		[
			new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" },
		]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<Profiles>();

		cut.WaitForAssertion(() => Assert.Contains("Deep thinker", cut.Markup));
		Assert.Contains("Claude · opus · high", cut.Markup);
		Assert.Contains("aria-label=\"Add a profile\"", cut.Markup);
	}

	[Fact]
	public void LeadSection_PickingAProfileLinksItAndHidesTheManualFields()
	{
		using var context = new BunitContext();
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		var thinker = new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" };
		var other = new Profile { Id = Guid.NewGuid(), Name = "Open coder", ProviderId = OpenCode.Id, Provider = OpenCode };
		var project = new ProjectModalFormModel();

		var cut = context.Render<ProjectLeadSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.AvailableProviders, [Claude])
			.Add(section => section.Profiles, [thinker, other]));

		// OpenCode can't lead, so its profile isn't offered.
		Assert.Empty(cut.FindAll($"#modal-leadProfile option[value='{other.Id}']"));
		Assert.NotEmpty(cut.FindAll("#modal-leadProvider"));

		cut.Find("#modal-leadProfile").Change(thinker.Id.ToString());

		Assert.Equal(thinker.Id, project.LeadProfileId);
		Assert.Equal((Claude.Id, "opus", "high"), (project.LeadProviderId!.Value, project.LeadModelId, project.LeadReasoningEffort));
		Assert.Empty(cut.FindAll("#modal-leadProvider"));
		Assert.NotEmpty(cut.FindAll("#modal-leadDirection"));

		cut.Find("#modal-leadProfile").Change(string.Empty);

		Assert.Null(project.LeadProfileId);
		Assert.Equal(Claude.Id, project.LeadProviderId);
		Assert.NotEmpty(cut.FindAll("#modal-leadProvider"));
	}

	[Fact]
	public void HelpersSection_OffersProfilesThatCanAnswer_AndLinksThePicks()
	{
		using var context = new BunitContext();
		var grok = new InferenceProvider { Id = Guid.NewGuid(), Name = "Grok", IsEnabled = true };
		var opus = new Profile { Id = Guid.NewGuid(), Name = "Opus", ProviderId = Claude.Id, Provider = Claude };
		var research = new Profile { Id = Guid.NewGuid(), Name = "Grok research", InferenceProviderId = grok.Id, InferenceProvider = grok };
		var openCoder = new Profile { Id = Guid.NewGuid(), Name = "Open coder", ProviderId = OpenCode.Id, Provider = OpenCode };
		var project = new ProjectModalFormModel();

		var cut = context.Render<ProjectHelpersSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.Profiles, [opus, research, openCoder])
			.Add(section => section.ReadOnlyProviders, [Claude])
			.Add(section => section.InferenceProviders, [grok]));

		Assert.Empty(cut.FindAll($"#modal-decisionsProfile option[value='{openCoder.Id}']"));
		cut.Find("#modal-decisionsProfile").Change(opus.Id.ToString());
		cut.Find("#modal-researchProfile").Change(research.Id.ToString());

		Assert.Equal(opus.Id, project.DecisionsProfileId);
		Assert.Equal(research.Id, project.ResearchProfileId);
		Assert.Contains("It sees only the question, not the code.", cut.Markup);
	}

	[Fact]
	public void DefaultProfileRow_SaysDefault_AndHasNoDelete()
	{
		using var context = new BunitContext();
		var profile = new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = Claude.Id, Provider = Claude, IsProviderDefault = true };

		var cut = context.Render<ProfileRow>(parameters => parameters.Add(row => row.Profile, profile));
		cut.Find("button[aria-expanded]").Click();

		Assert.Contains(">Default<", cut.Markup);
		Assert.DoesNotContain("Delete", cut.Markup);
	}

	[Fact]
	public void ProfileModal_OnAnInferenceProvider_OffersItsModelsAndNoReasoning()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		var grok = new InferenceProvider { Id = Guid.NewGuid(), Name = "Grok", IsEnabled = true };
		var profiles = new FakeProfileService([]);
		context.Services.AddSingleton<IProfileService>(profiles);
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceService(grok));

		var cut = context.Render<ProfileModal>(parameters => parameters.Add(modal => modal.IsVisible, true));
		Assert.NotEmpty(cut.FindAll("#profile-reasoning"));

		cut.Find("#profile-provider").Change($"i:{grok.Id}");
		cut.Find("#profile-name").Change("Grok research");
		cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#profile-model option[value='grok-4']")));
		Assert.Empty(cut.FindAll("#profile-reasoning"));
		cut.Find("#profile-model").Change("grok-4");
		cut.Find("form").Submit();

		cut.WaitForAssertion(() => Assert.NotNull(profiles.Created));
		Assert.Equal((null, grok.Id, "grok-4"), (profiles.Created!.ProviderId, profiles.Created.InferenceProviderId!.Value, profiles.Created.ModelId));
	}

	[Fact]
	public void AgentModal_PickingAProfileHidesTheManualProvider()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		var opus = new Profile { Id = Guid.NewGuid(), Name = "Opus", ProviderId = Claude.Id, Provider = Claude, ModelId = null, ReasoningEffort = "high" };
		context.Services.AddSingleton<IProfileService>(new FakeProfileService([opus]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		context.Services.AddSingleton<IAgentService>(new NoAgentService());
		context.Services.AddSingleton<ISkillService>(new NoSkillService());
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<AgentModal>(parameters => parameters.Add(modal => modal.IsVisible, true));
		cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#team-role-profile")));
		Assert.NotEmpty(cut.FindAll("#team-role-provider"));

		cut.Find("#team-role-profile").Change(opus.Id.ToString());

		cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#team-role-provider")));
		Assert.Contains("Claude · high", cut.Markup);
	}

	[Fact]
	public void AgentAssignment_PicksAProfile_AndHidesItsManualFieldsWhenLinked()
	{
		using var context = new BunitContext();
		var agent = new Agent { Id = Guid.NewGuid(), Name = "Reviewer", IsEnabled = true };
		var opus = new Profile { Id = Guid.NewGuid(), Name = "Opus", ProviderId = Claude.Id, Provider = Claude };
		var project = new ProjectModalFormModel();
		project.AgentAssignments.Add(new ProjectAgent { AgentId = agent.Id, ProviderId = Claude.Id });
		(Guid AgentId, Profile? Profile)? picked = null;

		IRenderedComponent<ProjectAgentsSection> Render() => context.Render<ProjectAgentsSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.AvailableAgents, [agent])
			.Add(section => section.AvailableProviders, [Claude])
			.Add(section => section.ResolveAgent, id => id == agent.Id ? agent : null)
			.Add(section => section.IsAgentAssigned, id => id == agent.Id)
			.Add(section => section.ResolveProviderModels, _ => [])
			.Add(section => section.Profiles, [opus])
			.Add(section => section.OnProfileChanged, EventCallback.Factory.Create<(Guid, Profile?)>(this, change => picked = change)));

		var cut = Render();
		Assert.NotEmpty(cut.FindAll($"#team-provider-{agent.Id}"));
		cut.Find($"#team-profile-{agent.Id}").Change(opus.Id.ToString());
		Assert.Equal(agent.Id, picked!.Value.AgentId);
		Assert.Equal(opus.Id, picked.Value.Profile!.Id);

		project.AgentAssignments.Single().ProfileId = opus.Id;
		var linked = Render();
		Assert.Empty(linked.FindAll($"#team-provider-{agent.Id}"));
		Assert.Contains("Editing the profile updates this too.", linked.Markup);
	}

	[Fact]
	public void ProfilePicker_RendersNothingWithoutProfiles()
	{
		using var context = new BunitContext();
		var cut = context.Render<ProfilePicker>(parameters => parameters.Add(picker => picker.Id, "picker"));
		Assert.Empty(cut.FindAll("select"));
	}

	private sealed class FakeProfileService(IReadOnlyList<Profile> profiles) : IProfileService
	{
		public Profile? Created { get; private set; }
		public Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Profile>>(profiles);
		public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));
		public Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(Created = profile);
		public Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class FakeProviderService : FakeProviderServiceBase
	{
		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Provider>>([Claude, OpenCode]);
	}

	private sealed class FakeInferenceService(InferenceProvider grok) : FakeInferenceProviderServiceBase
	{
		public override Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>([grok]);
		public override Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceModel>>([new InferenceModel { InferenceProviderId = grok.Id, ModelId = "grok-4", IsAvailable = true }]);
	}

	private sealed class NoAgentService : FakeAgentServiceBase
	{
	}

	private sealed class NoSkillService : ISkillService
	{
		public Task<IEnumerable<Skill>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Skill>>([]);
		public Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Skill>>([]);
		public Task<Skill?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Skill?>(null);
		public Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<Skill?>(null);
		public Task<Skill> CreateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Skill> UpdateAsync(Skill skill, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
		public Task<SkillImportPreview> PreviewImportAsync(SkillImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<SkillImportResult> ImportAsync(SkillImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> ExpandSkillAsync(string description, Guid providerId, string? modelId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
