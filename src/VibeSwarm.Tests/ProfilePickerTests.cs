using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Client.Components.Profiles;
using VibeSwarm.Client.Components.Templates;
using VibeSwarm.Client.Models;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// One profile choice stands in for a provider, a model and a reasoning level wherever a job is
/// set up: the picker itself, and the retry and template dialogs built on it.
/// </summary>
public sealed class ProfilePickerTests
{
	private static readonly Provider Claude = new() { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true, IsDefault = true };
	private static readonly Provider Copilot = new() { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
	private static readonly Provider OpenCode = new() { Id = Guid.NewGuid(), Name = "OpenCode", Type = ProviderType.OpenCode, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = false };

	private static readonly Profile ClaudeDefault = new() { Id = Guid.NewGuid(), Name = "Claude", ProviderId = Claude.Id, Provider = Claude, IsProviderDefault = true };
	private static readonly Profile Deep = new() { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" };
	private static readonly Profile Second = new() { Id = Guid.NewGuid(), Name = "Second opinion", ProviderId = Copilot.Id, Provider = Copilot, ModelId = "gpt-5" };
	private static readonly Profile SwitchedOff = new() { Id = Guid.NewGuid(), Name = "Switched off", ProviderId = OpenCode.Id, Provider = OpenCode };
	private static readonly Profile Answers = new() { Id = Guid.NewGuid(), Name = "Grok", InferenceProviderId = Guid.NewGuid(), InferenceProvider = new InferenceProvider { Name = "Grok", IsEnabled = true } };
	private static readonly Profile[] All = [ClaudeDefault, Deep, Second, SwitchedOff, Answers];

	[Fact]
	public void Picker_OffersTheProfilesThatFit_AndSaysWhatThePickedOneRunsOn()
	{
		using var context = new BunitContext();
		Profile? picked = null;
		var cut = context.Render<ProfilePicker>(parameters => parameters
			.Add(picker => picker.Id, "who")
			.Add(picker => picker.Label, "Run with")
			.Add(picker => picker.Profiles, All)
			.Add(picker => picker.Allowed, ProfileRoles.CanBuild)
			.Add(picker => picker.SelectedProfileId, Deep.Id)
			.Add(picker => picker.OnSelected, EventCallback.Factory.Create<Profile?>(this, profile => picked = profile)));

		// No Custom and, where a profile is required, no empty choice either.
		Assert.Equal(["Claude", "Deep thinker", "Second opinion"], cut.FindAll("#who option").Select(option => option.TextContent));
		Assert.Equal(Deep.Id.ToString(), cut.Find("#who").GetAttribute("value"));
		Assert.Contains("Claude · opus · high", cut.Markup);

		cut.Find("#who").Change(Second.Id.ToString());
		Assert.Same(Second, picked);
	}

	[Fact]
	public void Picker_KeepsOfferingTheProfileAlreadyPicked_EvenWhenItNoLongerFits()
	{
		using var context = new BunitContext();
		var cut = context.Render<ProfilePicker>(parameters => parameters
			.Add(picker => picker.Id, "who")
			.Add(picker => picker.Profiles, All)
			.Add(picker => picker.Allowed, ProfileRoles.CanBuild)
			.Add(picker => picker.SelectedProfileId, SwitchedOff.Id));

		// Its provider was turned off, but opening and saving the form must not change the pick.
		Assert.Contains("Switched off", cut.FindAll("#who option").Select(option => option.TextContent));
		Assert.Equal(SwitchedOff.Id.ToString(), cut.Find("#who").GetAttribute("value"));
	}

	[Fact]
	public void Picker_WithAnEmptyChoice_RaisesNullForIt_AndWithNothingToOfferSaysSo()
	{
		using var context = new BunitContext();
		Profile? picked = Deep;
		var optional = context.Render<ProfilePicker>(parameters => parameters
			.Add(picker => picker.Id, "review")
			.Add(picker => picker.Profiles, All)
			.Add(picker => picker.SelectedProfileId, Deep.Id)
			.Add(picker => picker.EmptyLabel, "Off")
			.Add(picker => picker.OnSelected, EventCallback.Factory.Create<Profile?>(this, profile => picked = profile)));

		Assert.Equal("Off", optional.FindAll("#review option")[0].TextContent);
		optional.Find("#review").Change(string.Empty);
		Assert.Null(picked);

		var nothing = context.Render<ProfilePicker>(parameters => parameters.Add(picker => picker.Id, "who"));
		Assert.Contains("No profile can do this yet", nothing.Markup);
		Assert.Contains("href=\"/providers\"", nothing.Markup);
	}

	[Fact]
	public async Task Picker_NewProfile_OpensTheSharedDialogForItsRole_AndPicksWhatWasAdded()
	{
		using var context = new BunitContext();
		var composer = new ProfileComposer();
		context.Services.AddSingleton(composer);
		Profile? picked = null;
		var cut = context.Render<ProfilePicker>(parameters => parameters
			.Add(picker => picker.Id, "who")
			.Add(picker => picker.Profiles, new[] { ClaudeDefault })
			.Add(picker => picker.Allowed, ProfileRoles.CanBuild)
			.Add(picker => picker.SelectedProfileId, ClaudeDefault.Id)
			.Add(picker => picker.OnSelected, EventCallback.Factory.Create<Profile?>(this, profile => picked = profile)));

		Assert.Equal("New profile…", cut.FindAll("#who option").Last().TextContent);
		var choosing = cut.Find("#who").ChangeAsync(new ChangeEventArgs { Value = "new" });

		// The dialog is told the role, so it offers only providers a job can run on.
		Assert.True(composer.IsOpen);
		Assert.True(composer.Allowed?.Invoke(Deep));
		Assert.False(composer.Allowed?.Invoke(Answers));

		await cut.InvokeAsync(() => composer.Complete(Deep));
		await choosing;

		Assert.Same(Deep, picked);
		Assert.False(composer.IsOpen);
		// A list loaded before the profile existed still offers it.
		Assert.Contains("Deep thinker", cut.FindAll("#who option").Select(option => option.TextContent));
	}

	[Fact]
	public void RetryDialog_StartsOnTheProfileTheJobRanOn_AndRetriesOnTheOnePicked()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(All));
		RetryJobModal.RetryOptions? retried = null;
		var job = new Job { Id = Guid.NewGuid(), ProviderId = Claude.Id, ModelUsed = "claude-opus-reported", ProfileId = Deep.Id, ProfileName = "Deep thinker" };

		var cut = context.Render<RetryJobModal>(parameters => parameters
			.Add(modal => modal.IsVisible, true)
			.Add(modal => modal.Job, job)
			.Add(modal => modal.OnRetry, EventCallback.Factory.Create<RetryJobModal.RetryOptions>(this, options => retried = options)));

		cut.WaitForAssertion(() => Assert.Equal(Deep.Id.ToString(), cut.Find("#retryProfile").GetAttribute("value")));
		Assert.Empty(cut.FindAll("#retryProvider"));
		Assert.Empty(cut.FindAll("#retryModel"));
		Assert.Empty(cut.FindAll("#retryReasoning"));

		cut.Find("#retryProfile").Change(Second.Id.ToString());
		cut.FindAll("button").Single(button => button.TextContent.Contains("Retry Job")).Click();

		Assert.Equal(Second.Id, retried?.ProfileId);
	}

	[Fact]
	public void RetryDialog_ForAJobFromBeforeProfiles_StartsOnTheProfileHoldingWhatItRanOn()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(All));
		var matching = new Job { Id = Guid.NewGuid(), ProviderId = Claude.Id, ModelUsed = "opus", ReasoningEffort = "high" };
		var unmatched = new Job { Id = Guid.NewGuid(), ProviderId = Copilot.Id, ModelUsed = "something-else" };

		var cut = context.Render<RetryJobModal>(parameters => parameters.Add(modal => modal.IsVisible, true).Add(modal => modal.Job, matching));
		cut.WaitForAssertion(() => Assert.Equal(Deep.Id.ToString(), cut.Find("#retryProfile").GetAttribute("value")));

		// Nothing holds what this one ran on, so it starts where a new job would.
		var other = context.Render<RetryJobModal>(parameters => parameters.Add(modal => modal.IsVisible, true).Add(modal => modal.Job, unmatched));
		other.WaitForAssertion(() => Assert.Equal(ClaudeDefault.Id.ToString(), other.Find("#retryProfile").GetAttribute("value")));
	}

	[Fact]
	public void RetryDialog_ForAJobFromBeforeProfiles_StartsOnWhatItsProjectBuildsWith()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(All));
		// A job is read without its project's Build order, so the dialog reads the project itself.
		var projectId = Guid.NewGuid();
		var project = new Project
		{
			Id = projectId,
			ProviderSelections = [new ProjectProvider { ProviderId = Claude.Id, ProfileId = Deep.Id, IsEnabled = true }]
		};
		context.Services.AddSingleton<IProjectService>(new ProjectWithBuildOrder(project));
		var job = new Job { Id = Guid.NewGuid(), ProjectId = projectId, Project = new Project { Id = projectId }, ProviderId = Claude.Id, ModelUsed = "claude-opus-reported" };

		var cut = context.Render<RetryJobModal>(parameters => parameters.Add(modal => modal.IsVisible, true).Add(modal => modal.Job, job));

		cut.WaitForAssertion(() => Assert.Equal(Deep.Id.ToString(), cut.Find("#retryProfile").GetAttribute("value")));
		Assert.Equal("Deep thinker", cut.FindAll("#retryProfile option")[0].TextContent);
	}

	private sealed class ProjectWithBuildOrder(Project project) : FakeProjectServiceBase
	{
		public override Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(id == project.Id ? project : null);
	}

	[Fact]
	public void TemplateEditor_PicksAProfileOrLeavesItToEachProject()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		var templates = new CapturingTemplateService();
		context.Services.AddSingleton<IJobTemplateService>(templates);
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(All));
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<TemplateEditorModal>(parameters => parameters.Add(modal => modal.IsVisible, true));

		cut.WaitForAssertion(() => Assert.Equal("Each project's Build profile", cut.FindAll("#template-profile option")[0].TextContent));
		Assert.Empty(cut.FindAll("#template-provider"));
		Assert.Empty(cut.FindAll("#template-model"));
		Assert.Empty(cut.FindAll("#template-reasoning"));

		cut.Find("#template-name").Change("Audit");
		cut.Find("#template-prompt").Input("Audit the auth code.");
		cut.Find("#template-profile").Change(Deep.Id.ToString());
		cut.Find("form").Submit();

		// Only the profile is sent; the server fills in what it holds.
		var saved = Assert.Single(templates.Created);
		Assert.Equal((Deep.Id, null, null), (saved.ProfileId, saved.ProviderId, saved.ModelId));
	}

	[Fact]
	public void TemplateFacts_NameTheProfile()
	{
		var template = new JobTemplate { Name = "Audit", GoalPrompt = "Audit.", ProfileId = Deep.Id, ProfileName = "Deep thinker", ProviderId = Claude.Id, ModelId = "opus" };
		Assert.Equal("Uses Deep thinker first", TemplateFacts.Describe(template));
		Assert.Null(TemplateFacts.Describe(new JobTemplate { Name = "Open", GoalPrompt = "Tidy." }));
	}

	[Fact]
	public void JobHeader_LeadsWithTheProfileAskedFor_ThenWhatRan()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		var cut = context.Render<JobHeaderSection>(parameters => parameters
			.Add(header => header.Status, JobStatus.Completed)
			.Add(header => header.JobTitle, "Polish the UI")
			.Add(header => header.ProfileName, "Deep thinker")
			.Add(header => header.ProviderName, "Copilot")
			.Add(header => header.ModelUsed, "gpt-5.4")
			.Add(header => header.CreatedAt, DateTime.UtcNow.AddMinutes(-10)));

		Assert.Contains("Deep thinker · Copilot / gpt-5.4", cut.Markup);
	}

	[Fact]
	public void JobProfiles_AreOfferedInTheProjectsBuildOrder_AndANewJobStartsOnItsFirst()
	{
		var project = new Project
		{
			ProviderSelections =
			[
				new ProjectProvider { ProviderId = Claude.Id, ProfileId = Deep.Id, Priority = 1, IsEnabled = true },
				new ProjectProvider { ProviderId = Copilot.Id, ProfileId = Second.Id, Priority = 0, IsEnabled = true },
			]
		};

		// Profiles that can't run a job (switched off, answers only) are never offered.
		Assert.Equal(["Second opinion", "Deep thinker", "Claude"], ProjectExecutionDefaults.OrderJobProfiles(project, All).Select(profile => profile.Name));
		Assert.Equal(Second.Id, ProjectExecutionDefaults.GetBuildProfileId(project, All));

		// A project with no Build order starts on the default provider's own profile.
		Assert.Equal(ClaudeDefault.Id, ProjectExecutionDefaults.GetBuildProfileId(new Project(), All));
		Assert.Null(ProjectExecutionDefaults.GetBuildProfileId(new Project(), [SwitchedOff, Answers]));
	}

	private sealed class FakeProfileService(IReadOnlyList<Profile> profiles) : IProfileService
	{
		public Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Profile>>(profiles);
		public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));
		public Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class CapturingTemplateService : IJobTemplateService
	{
		public List<JobTemplate> Created { get; } = [];

		public Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobTemplate>>([]);
		public Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<JobTemplate?>(null);
		public Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default)
		{
			Created.Add(template);
			return Task.FromResult(template);
		}

		public Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default) => Task.FromResult(template);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(new JobTemplate { Id = id, Name = "Template", GoalPrompt = "Prompt" });
		public Task<IReadOnlyList<VibeSwarm.Shared.Models.JobTemplateProjectRun>> GetRunsAsync(Guid templateId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<VibeSwarm.Shared.Models.JobTemplateProjectRun>>([]);
		public Task<VibeSwarm.Shared.Models.JobTemplateRunResult> RunAsync(Guid templateId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
			=> Task.FromResult(new VibeSwarm.Shared.Models.JobTemplateRunResult());
	}
}
