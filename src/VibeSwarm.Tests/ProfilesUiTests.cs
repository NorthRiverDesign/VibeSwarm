using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
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
	public void Team_IsOneSwitch_AndEachRoleTakesTheTeamsProfileOrTheProjectsOwn()
	{
		using var context = new BunitContext();
		var grok = new InferenceProvider { Id = Guid.NewGuid(), Name = "Grok", IsEnabled = true };
		var claudeDefault = new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = Claude.Id, Provider = Claude, IsProviderDefault = true };
		var thinker = new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" };
		var openCoder = new Profile { Id = Guid.NewGuid(), Name = "Open coder", ProviderId = OpenCode.Id, Provider = OpenCode };
		var grokProfile = new Profile { Id = Guid.NewGuid(), Name = "Grok research", InferenceProviderId = grok.Id, InferenceProvider = grok, ModelId = "grok-4" };
		// A provider that is turned off can't lead or review, whatever its kind.
		var copilotOff = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = false };
		var switchedOff = new Profile { Id = Guid.NewGuid(), Name = "Switched off", ProviderId = copilotOff.Id, Provider = copilotOff };
		var project = new ProjectModalFormModel();
		project.ProviderSelections.Add(new ProjectProvider { ProviderId = Claude.Id, ProfileId = claudeDefault.Id, IsEnabled = true });

		var cut = context.Render<ProjectTeamSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.Profiles, [claudeDefault, thinker, openCoder, grokProfile, switchedOff])
			.Add(section => section.TeamLeadProfileId, thinker.Id)
			.Add(section => section.TeamReviewProfileId, grokProfile.Id));

		// Off, the builder works alone: only its row shows.
		Assert.Equal(["Build"], RowTitles(cut));
		Assert.Contains("the builder works each job alone", cut.Markup);

		// One switch brings in the team's Lead and reviewer, in the order a job meets them.
		cut.Find("#modal-teamEnabled").Change(true);
		Assert.Equal(["Lead", "Build", "Review"], RowTitles(cut));
		Assert.True(project.TeamEnabled);
		Assert.Equal(TeamRoles.None, project.TeamOverrides);
		Assert.Equal((thinker.Id, Claude.Id, "opus", "high"), (project.LeadProfileId!.Value, project.LeadProviderId!.Value, project.LeadModelId, project.LeadReasoningEffort));
		Assert.True(project.ReviewEnabled);
		Assert.Equal((grokProfile.Id, null, grok.Id, "grok-4"), (project.ReviewProfileId!.Value, project.ReviewProviderId, project.ReviewInferenceProviderId!.Value, project.ReviewModelId));

		// The Lead reads the code, which OpenCode and an inference provider can't do for it. The
		// first choice follows the team.
		Assert.Contains("Deep thinker", OpenRow(cut, "Lead").TextContent);
		Assert.Equal(["Team's: Deep thinker", "Claude", "Deep thinker"], Options(cut, "#team-lead"));
		Assert.NotEmpty(cut.FindAll("#modal-leadDirection"));
		cut.Find("#team-lead").Change(claudeDefault.Id.ToString());
		Assert.Equal(TeamRoles.Lead, project.TeamOverrides);
		Assert.Equal(claudeDefault.Id, project.LeadProfileId);
		Assert.Contains("Claude · this project only", OpenRow(cut, "Lead").TextContent);

		// Picking the team's again drops the project's own choice.
		cut.Find("#team-lead").Change(string.Empty);
		Assert.Equal(TeamRoles.None, project.TeamOverrides);
		Assert.Equal(thinker.Id, project.LeadProfileId);

		// An inference profile can review, on the change alone.
		OpenRow(cut, "Review");
		Assert.Equal(["Team's: Grok research", "Claude", "Deep thinker", "Grok research"], Options(cut, "#team-review"));
		Assert.Contains("Reads the change only.", cut.Markup);
		cut.Find("#team-review").Change(thinker.Id.ToString());
		Assert.Equal(TeamRoles.Review, project.TeamOverrides);
		Assert.Equal((thinker.Id, Claude.Id), (project.ReviewProfileId!.Value, project.ReviewProviderId!.Value));

		// Off again leaves the builder alone, with nothing overridden.
		cut.Find("#modal-teamEnabled").Change(false);
		Assert.Equal(["Build"], RowTitles(cut));
		Assert.False(project.ReviewEnabled);
		Assert.Null(project.LeadProviderId);
		Assert.Equal(TeamRoles.None, project.TeamOverrides);

		// No role asks for a provider, a model or a reasoning level, and the helper roles are gone.
		Assert.Empty(cut.FindAll("#modal-leadProvider, #modal-reviewer, #modal-reviewModel, #modal-leadModel, #workflow-plan, #workflow-research, #workflow-decisions"));
	}

	[Fact]
	public void Team_WithNobodyOnIt_PointsToSettings()
	{
		using var context = new BunitContext();
		var claudeDefault = new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = Claude.Id, Provider = Claude, IsProviderDefault = true };
		var project = new ProjectModalFormModel();

		var cut = context.Render<ProjectTeamSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.Profiles, [claudeDefault]));
		cut.Find("#modal-teamEnabled").Change(true);

		Assert.Equal("/settings", cut.Find("a.fg-warning").GetAttribute("href"));
		Assert.Contains("Nobody leads yet", cut.Markup);
		Assert.Contains("Nobody reviews yet", cut.Markup);
		Assert.False(project.ReviewEnabled);
		Assert.Null(project.LeadProviderId);
	}

	[Fact]
	public void Team_Build_HasOneBuilderAndFallbacksOnOtherProviders()
	{
		using var context = new BunitContext();
		var copilot = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
		var claudeDefault = new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = Claude.Id, Provider = Claude, IsProviderDefault = true };
		var thinker = new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" };
		var second = new Profile { Id = Guid.NewGuid(), Name = "Second opinion", ProviderId = copilot.Id, Provider = copilot, ModelId = "gpt-5" };
		var project = new ProjectModalFormModel();
		project.ProviderSelections.Add(new ProjectProvider { ProviderId = Claude.Id, ProfileId = claudeDefault.Id, IsEnabled = true });

		var cut = context.Render<ProjectTeamSection>(parameters => parameters
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

	private static List<string> RowTitles(IRenderedComponent<ProjectTeamSection> cut)
		=> cut.FindAll("button[aria-expanded] .fw-medium, button[aria-expanded] .fw-semibold").Select(title => title.TextContent.Trim()).ToList();

	private static AngleSharp.Dom.IElement OpenRow(IRenderedComponent<ProjectTeamSection> cut, string title)
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
