using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Components.Templates;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class TemplatesPageTests
{
	private static readonly Project Alpha = new() { Id = Guid.NewGuid(), Name = "Alpha", WorkingPath = "/tmp/alpha" };
	private static readonly Project Beta = new() { Id = Guid.NewGuid(), Name = "Beta", WorkingPath = "/tmp/beta" };
	private static readonly Project Paused = new() { Id = Guid.NewGuid(), Name = "Paused", WorkingPath = "/tmp/paused", IsActive = false };

	[Fact]
	public void TemplatesPage_ListsTemplatesWithWhereTheyRan()
	{
		var templates = new FakeJobTemplateService(
		[
			new JobTemplate { Id = Guid.NewGuid(), Name = "Update dependencies", GoalPrompt = "Bring every dependency up to date.", ProjectRunCount = 3, LastRunAt = DateTime.UtcNow.AddDays(-2) },
			new JobTemplate { Id = Guid.NewGuid(), Name = "Compliance pass", GoalPrompt = "Check age gates.", Description = "Under-13 sign-ups" }
		]);
		using var context = CreateContext(templates);

		var cut = context.Render<Templates>();

		cut.WaitForAssertion(() =>
		{
			Assert.Contains("Update dependencies", cut.Markup);
			Assert.Contains("Ran on 3 projects · 2d ago", cut.Markup);
			Assert.Contains("Under-13 sign-ups", cut.Markup);
			Assert.Contains("list-group rounded-9", cut.Markup);
		});
	}

	[Fact]
	public void TemplatesPage_EmptyLibrary_OffersToWriteOne()
	{
		using var context = CreateContext(new FakeJobTemplateService([]));

		var cut = context.Render<Templates>();

		cut.WaitForAssertion(() => Assert.Contains("No templates yet", cut.Markup));
	}

	[Fact]
	public void TemplatesPage_RunOnTickedProjects_PostsTheirIds_AndHidesInactiveOnes()
	{
		var template = new JobTemplate { Id = Guid.NewGuid(), Name = "Update dependencies", GoalPrompt = "Bring every dependency up to date." };
		var templates = new FakeJobTemplateService([template]);
		using var context = CreateContext(templates);

		var cut = context.Render<Templates>();
		cut.WaitForAssertion(() => Assert.Contains("Update dependencies", cut.Markup));
		cut.Find("button.list-group-item").Click();
		cut.WaitForAssertion(() => Assert.Contains("Run on projects", cut.Markup));
		cut.FindAll("button").First(button => button.TextContent.Contains("Run on projects", StringComparison.Ordinal)).Click();

		cut.WaitForAssertion(() => Assert.Contains($"run-template-project-{Beta.Id:N}", cut.Markup));
		Assert.DoesNotContain("Paused", cut.Markup);
		cut.Find($"#run-template-project-{Beta.Id:N}").Change(true);
		cut.WaitForAssertion(() => Assert.Contains("Run on 1 project", cut.Markup));
		cut.FindAll("button").First(button => button.TextContent.Contains("Run on 1 project", StringComparison.Ordinal)).Click();

		cut.WaitForAssertion(() => Assert.Equal([Beta.Id], Assert.Single(templates.Runs).ProjectIds));
		Assert.Equal(template.Id, templates.Runs[0].TemplateId);
	}

	[Fact]
	public void TemplatesPage_ProjectAlreadyRunningTheTemplate_CannotBePickedAgain()
	{
		var template = new JobTemplate { Id = Guid.NewGuid(), Name = "Update dependencies", GoalPrompt = "Bring every dependency up to date." };
		var templates = new FakeJobTemplateService([template])
		{
			History = [new JobTemplateProjectRun { ProjectId = Alpha.Id, ProjectName = "Alpha", JobId = Guid.NewGuid(), Status = JobStatus.Processing, CreatedAt = DateTime.UtcNow }]
		};
		using var context = CreateContext(templates);

		var cut = context.Render<Templates>();
		cut.WaitForAssertion(() => Assert.Contains("Update dependencies", cut.Markup));
		cut.Find("button.list-group-item").Click();
		cut.WaitForAssertion(() => Assert.Contains("Runs", cut.Markup));
		cut.FindAll("button").First(button => button.TextContent.Contains("Run on projects", StringComparison.Ordinal)).Click();

		cut.WaitForAssertion(() => Assert.True(cut.Find($"#run-template-project-{Alpha.Id:N}").HasAttribute("disabled")));
		Assert.Contains("Already running this template", cut.Markup);
		Assert.False(cut.Find($"#run-template-project-{Beta.Id:N}").HasAttribute("disabled"));
	}

	[Fact]
	public void PickTemplateModal_RunsTheTemplateOnTheViewedProject()
	{
		var template = new JobTemplate { Id = Guid.NewGuid(), Name = "Update dependencies", GoalPrompt = "Bring every dependency up to date." };
		var templates = new FakeJobTemplateService([template]);
		using var context = CreateContext(templates);
		var closed = false;

		var cut = context.Render<PickTemplateModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.IsVisibleChanged, visible => closed = !visible)
			.Add(component => component.ProjectId, Alpha.Id)
			.Add(component => component.ProjectName, "Alpha"));

		cut.WaitForAssertion(() => Assert.Contains("Update dependencies", cut.Markup));
		cut.Find("button.list-group-item").Click();
		cut.FindAll("button").First(button => button.TextContent.Contains("Run on Alpha", StringComparison.Ordinal)).Click();

		cut.WaitForAssertion(() => Assert.True(closed));
		var run = Assert.Single(templates.Runs);
		Assert.Equal(template.Id, run.TemplateId);
		Assert.Equal([Alpha.Id], run.ProjectIds);
	}

	[Fact]
	public void PickTemplateModal_SkippedRun_StaysOpenWithTheReason()
	{
		var template = new JobTemplate { Id = Guid.NewGuid(), Name = "Update dependencies", GoalPrompt = "Bring every dependency up to date." };
		var templates = new FakeJobTemplateService([template]) { SkipReason = "Already running this template." };
		using var context = CreateContext(templates);
		var closed = false;

		var cut = context.Render<PickTemplateModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.IsVisibleChanged, visible => closed = !visible)
			.Add(component => component.ProjectId, Alpha.Id)
			.Add(component => component.ProjectName, "Alpha"));

		cut.WaitForAssertion(() => Assert.Contains("Update dependencies", cut.Markup));
		cut.Find("button.list-group-item").Click();
		cut.FindAll("button").First(button => button.TextContent.Contains("Run on Alpha", StringComparison.Ordinal)).Click();

		cut.WaitForAssertion(() => Assert.Contains("Already running this template.", cut.Markup));
		Assert.False(closed);
	}

	[Fact]
	public void TemplateFacts_DescribesOnlyWhatTheTemplateOverrides()
	{
		Assert.Null(TemplateFacts.Describe(new JobTemplate { Name = "Plain", GoalPrompt = "Do it" }));
		Assert.Equal(
			"Uses branch chore/deps, pull request into main",
			TemplateFacts.Describe(new JobTemplate
			{
				Name = "Deps",
				GoalPrompt = "Do it",
				Branch = "chore/deps",
				GitChangeDeliveryMode = GitChangeDeliveryMode.PullRequest,
				TargetBranch = "main"
			}));
	}

	private static BunitContext CreateContext(FakeJobTemplateService templates)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IJobTemplateService>(templates);
		context.Services.AddSingleton<IProjectService>(new FakeProjectService([Alpha, Beta, Paused]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		context.Services.AddSingleton<NotificationService>();
		return context;
	}

	private sealed class FakeJobTemplateService(List<JobTemplate> templates) : IJobTemplateService
	{
		public List<(Guid TemplateId, List<Guid> ProjectIds)> Runs { get; } = [];
		public List<JobTemplateProjectRun> History { get; init; } = [];
		public string? SkipReason { get; init; }

		public Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<JobTemplate>>(templates);
		public Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(templates.FirstOrDefault(template => template.Id == id));
		public Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default) => Task.FromResult(template);
		public Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default) => Task.FromResult(template);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<JobTemplateProjectRun>> GetRunsAsync(Guid templateId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<JobTemplateProjectRun>>(History);

		public Task<JobTemplateRunResult> RunAsync(Guid templateId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
		{
			Runs.Add((templateId, projectIds.ToList()));
			return Task.FromResult(new JobTemplateRunResult
			{
				Projects = projectIds.Select(id => new JobTemplateRunOutcome
				{
					ProjectId = id,
					ProjectName = id == Alpha.Id ? "Alpha" : "Beta",
					JobId = SkipReason == null ? Guid.NewGuid() : null,
					SkipReason = SkipReason
				}).ToList()
			});
		}
	}

	private sealed class FakeProjectService(IReadOnlyList<Project> projects) : FakeProjectServiceBase
	{
		public override Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Project>>(projects);
	}

	private sealed class FakeProviderService : FakeProviderServiceBase
	{
		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Provider>>([]);
	}
}
