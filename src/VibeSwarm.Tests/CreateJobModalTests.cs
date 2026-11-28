using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class CreateJobModalTests
{
	[Fact]
	public void CreateJobModal_RendersTemplateLibraryControls()
	{
		using var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([]));
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
			.Add(component => component.Branches, new List<GitBranchInfo>())
			.Add(component => component.TemplateLibrary, [template]));

		Assert.Contains("Template Library", cut.Markup);
		Assert.Contains("Start from scratch...", cut.Markup);
		Assert.Contains("Fix bug", cut.Markup);
		Assert.Contains("Save as Template", cut.Markup);
	}

	[Fact]
	public void CreateJobModal_GoalPromptLongerThanTwoThousandCharacters_CountsAndSubmits()
	{
		using var context = CreateGoalPromptContext();
		var provider = new Provider { Id = Guid.NewGuid(), Name = "GitHub Copilot", Type = ProviderType.Copilot, IsEnabled = true };
		var submitted = false;
		var job = new Job { ProviderId = provider.Id };
		var cut = RenderGoalPromptModal(context, provider, job, () => submitted = true);

		Assert.Equal($"0/{ValidationLimits.JobGoalPromptMaxLength:N0} characters", cut.Find("#goalPromptLength").TextContent);
		Assert.Equal(ValidationLimits.JobGoalPromptMaxLength.ToString(), cut.Find("#goalPrompt").GetAttribute("maxlength"));

		var prompt = new string('a', 5000);
		cut.Find("#goalPrompt").Input(prompt);

		Assert.Equal($"{5000:N0}/{ValidationLimits.JobGoalPromptMaxLength:N0} characters", cut.Find("#goalPromptLength").TextContent);

		cut.Find("form").Submit();

		Assert.True(submitted);
		Assert.Equal(prompt, job.GoalPrompt);
	}

	[Fact]
	public void CreateJobModal_GoalPromptOverTheLimit_IsRejected()
	{
		using var context = CreateGoalPromptContext();
		var provider = new Provider { Id = Guid.NewGuid(), Name = "GitHub Copilot", Type = ProviderType.Copilot, IsEnabled = true };
		var submitted = false;
		var cut = RenderGoalPromptModal(context, provider, new Job { ProviderId = provider.Id }, () => submitted = true);

		cut.Find("#goalPrompt").Input(new string('a', ValidationLimits.JobGoalPromptMaxLength + 1));
		cut.Find("form").Submit();

		Assert.False(submitted);
		Assert.Contains("fg-danger", cut.Find("#goalPromptLength").ClassList);
		Assert.Contains($"Goal prompt must be {ValidationLimits.JobGoalPromptMaxLength:N0} characters or fewer.", cut.Markup);
	}

	[Fact]
	public void CreateJobModal_TypingAGoalPrompt_ClearsTheRequiredMessage()
	{
		using var context = CreateGoalPromptContext();
		var provider = new Provider { Id = Guid.NewGuid(), Name = "GitHub Copilot", Type = ProviderType.Copilot, IsEnabled = true };
		var cut = RenderGoalPromptModal(context, provider, new Job(), () => { });

		cut.Find("form").Submit();
		Assert.Contains("Goal prompt is required.", cut.Markup);

		cut.Find("#goalPrompt").Input("Fix the flaky test");

		Assert.DoesNotContain("Goal prompt is required.", cut.Markup);
	}

	[Fact]
	public void CreateJobModal_StartsOnTheProjectsBuildProfile_AndAsksForNoProviderOrModel()
	{
		using var context = CreateGoalPromptContext();
		var (claude, copilot) = (NewProvider("Claude", ProviderType.Claude, isDefault: true), NewProvider("GitHub Copilot", ProviderType.Copilot));
		var claudeDefault = new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = claude.Id, Provider = claude, IsProviderDefault = true };
		var build = new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = copilot.Id, Provider = copilot, ModelId = "gpt-5", ReasoningEffort = "high" };
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(claudeDefault, build));
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			ProviderSelections = [new ProjectProvider { ProviderId = copilot.Id, ProfileId = build.Id, IsEnabled = true }]
		};
		var submitted = false;
		var job = new Job();
		var cut = context.Render<CreateJobModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.JobModel, job)
			.Add(component => component.Project, project)
			.Add(component => component.Branches, new List<GitBranchInfo>())
			.Add(component => component.TemplateLibrary, [])
			.Add(component => component.OnSubmit, EventCallback.Factory.Create(this, () => submitted = true)));

		// One choice, already on the profile the project builds with, and saying what that is.
		Assert.Equal(build.Id.ToString(), cut.Find("#jobProfile").GetAttribute("value"));
		Assert.Contains("GitHub Copilot · gpt-5 · high", cut.Markup);
		Assert.Equal(["Deep thinker", "Claude"], cut.FindAll("#jobProfile option").Select(option => option.TextContent));
		Assert.Empty(cut.FindAll("#provider"));
		Assert.Empty(cut.FindAll("#model"));
		Assert.Empty(cut.FindAll("#reasoning"));

		cut.Find("#goalPrompt").Input("Fix the flaky test");
		cut.Find("form").Submit();

		// The job names the profile and leaves the provider, model and reasoning to the server.
		Assert.True(submitted);
		Assert.Equal(build.Id, job.ProfileId);
		Assert.Equal((Guid.Empty, null, null), (job.ProviderId, job.ModelUsed, job.ReasoningEffort));
	}

	[Fact]
	public void CreateJobModal_PickingAProfileAndInstances_NamesItOnTheJobAndAsksForCopies()
	{
		using var context = CreateGoalPromptContext();
		var (claude, copilot) = (NewProvider("Claude", ProviderType.Claude, isDefault: true), NewProvider("GitHub Copilot", ProviderType.Copilot));
		var claudeDefault = new Profile { Id = Guid.NewGuid(), Name = "Claude", ProviderId = claude.Id, Provider = claude, IsProviderDefault = true };
		var profile = new Profile { Id = Guid.NewGuid(), Name = "Fast fixer", ProviderId = copilot.Id, Provider = copilot, ModelId = "gpt-5", ReasoningEffort = "high" };
		// A profile that only answers questions, and one on a provider that is off, can't run a job.
		var answers = new Profile { Id = Guid.NewGuid(), Name = "Grok", InferenceProviderId = Guid.NewGuid(), InferenceProvider = new InferenceProvider { Name = "Grok", IsEnabled = true } };
		var off = new Profile { Id = Guid.NewGuid(), Name = "Switched off", ProviderId = Guid.NewGuid(), Provider = new Provider { Name = "OpenCode", Type = ProviderType.OpenCode, IsEnabled = false } };
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(claudeDefault, profile, answers, off));
		var submitted = false;
		var job = new Job();
		var cut = context.Render<CreateJobModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.JobModel, job)
			.Add(component => component.Branches, new List<GitBranchInfo>())
			.Add(component => component.TemplateLibrary, [])
			.Add(component => component.OnSubmit, EventCallback.Factory.Create(this, () => submitted = true)));

		Assert.Equal(["Claude", "Fast fixer"], cut.FindAll("#jobProfile option").Select(option => option.TextContent));
		// With no Build order to go by, the job starts on the default provider's own profile.
		Assert.Equal(claudeDefault.Id.ToString(), cut.Find("#jobProfile").GetAttribute("value"));

		cut.Find("#jobProfile").Change(profile.Id.ToString());
		cut.Find("#instances").Change("3");
		cut.Find("#goalPrompt").Input("- One\n- Two\n- Three");
		cut.Find("form").Submit();

		Assert.True(submitted);
		Assert.Equal(profile.Id, job.ProfileId);
		Assert.Equal(3, job.RequestedInstances);
	}

	private static Provider NewProvider(string name, ProviderType type, bool isDefault = false)
		=> new() { Id = Guid.NewGuid(), Name = name, Type = type, IsEnabled = true, IsDefault = isDefault };

	private sealed class FakeProfileService(params Profile[] profiles) : IProfileService
	{
		public Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Profile>>(profiles);
		public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));
		public Task<Profile> CreateAsync(Profile created, CancellationToken cancellationToken = default) => Task.FromResult(created);
		public Task<Profile> UpdateAsync(Profile updated, CancellationToken cancellationToken = default) => Task.FromResult(updated);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private static BunitContext CreateGoalPromptContext()
	{
		var context = new BunitContext();
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([]));
		context.Services.AddSingleton<IJobTemplateService>(new FakeJobTemplateService());
		context.Services.AddSingleton<NotificationService>();
		return context;
	}

	private IRenderedComponent<CreateJobModal> RenderGoalPromptModal(BunitContext context, Provider provider, Job job, Action onSubmit)
	{
		return context.Render<CreateJobModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.JobModel, job)
			.Add(component => component.Branches, new List<GitBranchInfo>())
			.Add(component => component.TemplateLibrary, [])
			.Add(component => component.OnSubmit, EventCallback.Factory.Create(this, onSubmit)));
	}

	[Fact]
	public void CreateJobModal_Templates_ChangeTheProfileOnlyWhenTheyNameOne()
	{
		using var context = CreateGoalPromptContext();
		var claude = NewProvider("Claude Code", ProviderType.Claude, isDefault: true);
		var build = new Profile { Id = Guid.NewGuid(), Name = "Claude Code", ProviderId = claude.Id, Provider = claude, IsProviderDefault = true };
		var careful = new Profile { Id = Guid.NewGuid(), Name = "Careful", ProviderId = claude.Id, Provider = claude, ModelId = "opus", ReasoningEffort = "high" };
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(build, careful));
		var leavesItToTheProject = new JobTemplate { Id = Guid.NewGuid(), Name = "Update dependencies", GoalPrompt = "Bring every dependency up to date." };
		var namesAProfile = new JobTemplate { Id = Guid.NewGuid(), Name = "Audit", GoalPrompt = "Audit the auth code.", ProfileId = careful.Id };
		var job = new Job { Branch = "develop", GitChangeDeliveryMode = GitChangeDeliveryMode.PullRequest, TargetBranch = "main" };
		var submitted = false;

		var cut = context.Render<CreateJobModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.JobModel, job)
			.Add(component => component.Branches, new List<GitBranchInfo>())
			.Add(component => component.TemplateLibrary, [leavesItToTheProject, namesAProfile])
			.Add(component => component.OnSubmit, EventCallback.Factory.Create(this, () => submitted = true)));

		// A template that leaves everything to the project keeps its profile, branch and delivery.
		cut.Find("#jobTemplate").Change(leavesItToTheProject.Id.ToString());
		Assert.Equal(build.Id.ToString(), cut.Find("#jobProfile").GetAttribute("value"));

		cut.Find("#jobTemplate").Change(namesAProfile.Id.ToString());
		Assert.Equal(careful.Id.ToString(), cut.Find("#jobProfile").GetAttribute("value"));

		cut.Find("form").Submit();

		Assert.True(submitted);
		Assert.Equal(namesAProfile.Id, job.JobTemplateId);
		Assert.Equal("Audit the auth code.", job.GoalPrompt);
		Assert.Equal(careful.Id, job.ProfileId);
		Assert.Equal("develop", job.Branch);
		Assert.Equal(GitChangeDeliveryMode.PullRequest, job.GitChangeDeliveryMode);
		Assert.Equal("main", job.TargetBranch);
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
		public Task<IReadOnlyList<VibeSwarm.Shared.Models.JobTemplateProjectRun>> GetRunsAsync(Guid templateId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<VibeSwarm.Shared.Models.JobTemplateProjectRun>>([]);
		public Task<VibeSwarm.Shared.Models.JobTemplateRunResult> RunAsync(Guid templateId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
			=> Task.FromResult(new VibeSwarm.Shared.Models.JobTemplateRunResult());
	}

	private sealed class FakeProjectService(IReadOnlyList<Project> projects, Project? detailedProject = null) : FakeProjectServiceBase
	{
		public override Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Project>>(projects);

		public override Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Project>>([]);

		public override Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult(detailedProject?.Id == id ? detailedProject : projects.FirstOrDefault(project => project.Id == id));

		public override Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default)
			=> Task.FromResult<Project?>(null);

		public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
		public override Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
		public override Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
		public override Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
	}

}
