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
	public void Workflow_EachRoleIsOffOrAProfile_AndOffersOnlyProfilesThatCanFillIt()
	{
		using var context = new BunitContext();
		var grok = new InferenceProvider { Id = Guid.NewGuid(), Name = "Grok", IsEnabled = true };
		var claudeDefault = new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = Claude.Id, Provider = Claude, IsProviderDefault = true };
		var thinker = new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" };
		var openCoder = new Profile { Id = Guid.NewGuid(), Name = "Open coder", ProviderId = OpenCode.Id, Provider = OpenCode };
		var grokProfile = new Profile { Id = Guid.NewGuid(), Name = "Grok research", InferenceProviderId = grok.Id, InferenceProvider = grok, ModelId = "grok-4" };
		var project = new ProjectModalFormModel();
		project.ProviderSelections.Add(new ProjectProvider { ProviderId = Claude.Id, ProfileId = claudeDefault.Id, IsEnabled = true });

		var cut = context.Render<ProjectWorkflowSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.Profiles, [claudeDefault, thinker, openCoder, grokProfile]));

		// Closed, the list reads as who does what: the builder by name, every other role Off.
		Assert.Equal(["Build", "Plan first", "Review", "Research", "Decisions", "Lead"], RowTitles(cut));
		Assert.Equal(5, cut.FindAll("button[aria-expanded]").Count(row => row.TextContent.Contains("Off")));
		Assert.Contains("Claude", OpenRow(cut, "Build").TextContent);

		// The Lead reads the code, which OpenCode and an inference provider can't do for it.
		OpenRow(cut, "Lead");
		Assert.Equal(["Off", "Claude", "Deep thinker"], Options(cut, "#workflow-lead"));
		cut.Find("#workflow-lead").Change(thinker.Id.ToString());
		Assert.Equal((thinker.Id, Claude.Id, "opus", "high"), (project.LeadProfileId!.Value, project.LeadProviderId!.Value, project.LeadModelId, project.LeadReasoningEffort));
		Assert.NotEmpty(cut.FindAll("#modal-leadDirection"));

		// Off stops it: the Lead runs for as long as it has a provider, so that goes too.
		cut.Find("#workflow-lead").Change(string.Empty);
		Assert.Null(project.LeadProfileId);
		Assert.Null(project.LeadProviderId);
		Assert.Empty(cut.FindAll("#modal-leadDirection"));

		// An inference profile can review, on the change alone.
		OpenRow(cut, "Review");
		Assert.Equal(["Off", "Claude", "Deep thinker", "Grok research"], Options(cut, "#workflow-review"));
		cut.Find("#workflow-review").Change(grokProfile.Id.ToString());
		Assert.True(project.ReviewEnabled);
		Assert.Equal((grokProfile.Id, null, grok.Id, "grok-4"), (project.ReviewProfileId!.Value, project.ReviewProviderId, project.ReviewInferenceProviderId!.Value, project.ReviewModelId));
		Assert.Contains("Reads the change only.", cut.Markup);
		cut.Find("#workflow-review").Change(string.Empty);
		Assert.False(project.ReviewEnabled);

		OpenRow(cut, "Plan first");
		Assert.Equal(["Off", "Claude", "Deep thinker"], Options(cut, "#workflow-plan"));
		cut.Find("#workflow-plan").Change(thinker.Id.ToString());
		Assert.Equal((true, thinker.Id, Claude.Id), (project.PlanningEnabled, project.PlanningProfileId!.Value, project.PlanningProviderId!.Value));
		cut.Find("#workflow-plan").Change(string.Empty);
		Assert.False(project.PlanningEnabled);

		OpenRow(cut, "Research");
		OpenRow(cut, "Decisions");
		cut.Find("#workflow-research").Change(grokProfile.Id.ToString());
		cut.Find("#workflow-decisions").Change(thinker.Id.ToString());
		Assert.Equal((grokProfile.Id, thinker.Id), (project.ResearchProfileId!.Value, project.DecisionsProfileId!.Value));
		Assert.Contains("Sees only the question, not the code.", cut.Markup);
		Assert.Contains("Reads the repository to answer.", cut.Markup);

		// No role asks for a provider, a model or a reasoning level.
		Assert.Empty(cut.FindAll("#modal-leadProvider, #modal-planningProvider, #modal-reviewer, #modal-planningModel, #modal-reviewModel, #modal-leadModel"));
	}

	[Fact]
	public void Workflow_Build_HasOneBuilderAndFallbacksOnOtherProviders()
	{
		using var context = new BunitContext();
		var copilot = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
		var claudeDefault = new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = Claude.Id, Provider = Claude, IsProviderDefault = true };
		var thinker = new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" };
		var second = new Profile { Id = Guid.NewGuid(), Name = "Second opinion", ProviderId = copilot.Id, Provider = copilot, ModelId = "gpt-5" };
		var project = new ProjectModalFormModel();
		project.ProviderSelections.Add(new ProjectProvider { ProviderId = Claude.Id, ProfileId = claudeDefault.Id, IsEnabled = true });

		var cut = context.Render<ProjectWorkflowSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.Profiles, [claudeDefault, thinker, second]));
		OpenRow(cut, "Build");

		Assert.Equal(claudeDefault.Id.ToString(), cut.Find("#workflow-build").GetAttribute("value"));
		Assert.Contains("a job waits when this profile's provider is out of usage", cut.Markup);

		cut.Find("#workflow-build").Change(thinker.Id.ToString());
		var builder = Assert.Single(project.ProviderSelections);
		Assert.Equal((thinker.Id, Claude.Id, "opus", "high"), (builder.ProfileId!.Value, builder.ProviderId, builder.PreferredModelId, builder.PreferredReasoningEffort));

		// A project lists a provider once, so only a profile on another provider can be a fallback.
		Assert.Equal(["Add a fallback…", "Second opinion"], Options(cut, "#workflow-fallback"));
		cut.Find("#workflow-fallback").Change(second.Id.ToString());
		Assert.Equal([(thinker.Id, 0), (second.Id, 1)], project.ProviderSelections.OrderBy(entry => entry.Priority).Select(entry => (entry.ProfileId!.Value, entry.Priority)));
		Assert.Contains("Deep thinker, then Second opinion", cut.Markup);
		Assert.Equal(["Add a fallback…"], Options(cut, "#workflow-fallback"));

		// Building with the fallback's provider moves it up rather than listing it twice.
		cut.Find("#workflow-build").Change(second.Id.ToString());
		Assert.Equal(second.Id, Assert.Single(project.ProviderSelections).ProfileId);

		cut.Find("#workflow-fallback").Change(thinker.Id.ToString());
		cut.FindAll("button").Single(button => button.GetAttribute("aria-label") == "Remove Deep thinker").Click();
		Assert.Equal(second.Id, Assert.Single(project.ProviderSelections).ProfileId);
	}

	private static List<string> RowTitles(IRenderedComponent<ProjectWorkflowSection> cut)
		=> cut.FindAll("button[aria-expanded] .fw-medium, button[aria-expanded] .fw-semibold").Select(title => title.TextContent.Trim()).ToList();

	private static AngleSharp.Dom.IElement OpenRow(IRenderedComponent<ProjectWorkflowSection> cut, string title)
	{
		var row = cut.FindAll("button[aria-expanded]").Single(button => button.TextContent.TrimStart().StartsWith(title, StringComparison.Ordinal));
		if (row.GetAttribute("aria-expanded") != "true")
		{
			row.Click();
		}

		return cut.FindAll("button[aria-expanded]").Single(button => button.TextContent.TrimStart().StartsWith(title, StringComparison.Ordinal));
	}

	private static List<string> Options<TComponent>(IRenderedComponent<TComponent> cut, string select) where TComponent : IComponent
		=> cut.FindAll($"{select} option").Select(option => option.TextContent).ToList();

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
	public void AgentModal_RunsOnAProfile_OrLeavesTheChoiceToEachProject()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		var opus = new Profile { Id = Guid.NewGuid(), Name = "Opus", ProviderId = Claude.Id, Provider = Claude, ModelId = null, ReasoningEffort = "high" };
		context.Services.AddSingleton<IProfileService>(new FakeProfileService([opus]));
		context.Services.AddSingleton<IAgentService>(new NoAgentService());
		context.Services.AddSingleton<ISkillService>(new NoSkillService());
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<AgentModal>(parameters => parameters.Add(modal => modal.IsVisible, true));
		cut.WaitForAssertion(() => Assert.Equal(["Choose in each project", "Opus"], Options(cut, "#team-role-profile")));
		Assert.Empty(cut.FindAll("#team-role-provider, #team-role-model, #team-role-reasoning"));

		cut.Find("#team-role-profile").Change(opus.Id.ToString());

		Assert.Contains("Claude · high", cut.Markup);
		Assert.Empty(cut.FindAll("#team-role-provider, #team-role-model, #team-role-reasoning"));
	}

	[Fact]
	public void AgentAssignment_RunsOnAProfile_WithNoProviderOrModelToSet()
	{
		using var context = new BunitContext();
		var agent = new Agent { Id = Guid.NewGuid(), Name = "Reviewer", IsEnabled = true };
		var opus = new Profile { Id = Guid.NewGuid(), Name = "Opus", ProviderId = Claude.Id, Provider = Claude };
		var fast = new Profile { Id = Guid.NewGuid(), Name = "Fast", ProviderId = Claude.Id, Provider = Claude, ModelId = "haiku" };
		var project = new ProjectModalFormModel();
		project.AgentAssignments.Add(new ProjectAgent { AgentId = agent.Id, ProviderId = Claude.Id, ProfileId = opus.Id });
		(Guid AgentId, Profile? Profile)? picked = null;

		var cut = context.Render<ProjectAgentsSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.AvailableAgents, [agent])
			.Add(section => section.ResolveAgent, id => id == agent.Id ? agent : null)
			.Add(section => section.IsAgentAssigned, id => id == agent.Id)
			.Add(section => section.Profiles, [opus, fast])
			.Add(section => section.OnProfileChanged, EventCallback.Factory.Create<(Guid, Profile?)>(this, change => picked = change)));

		Assert.Equal(opus.Id.ToString(), cut.Find($"#team-profile-{agent.Id}").GetAttribute("value"));
		Assert.Empty(cut.FindAll($"#team-provider-{agent.Id}, #team-model-{agent.Id}, #team-reasoning-{agent.Id}"));

		cut.Find($"#team-profile-{agent.Id}").Change(fast.Id.ToString());

		Assert.Equal(agent.Id, picked!.Value.AgentId);
		Assert.Equal(fast.Id, picked.Value.Profile!.Id);
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
