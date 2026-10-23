using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectsSyncMenuTests
{
	private readonly Project _api = new() { Id = Guid.NewGuid(), Name = "Api", WorkingPath = "/src/api", IsActive = true };
	private readonly Project _web = new() { Id = Guid.NewGuid(), Name = "Web", WorkingPath = "/src/web", IsActive = true };
	private readonly Project _notes = new() { Id = Guid.NewGuid(), Name = "Notes", WorkingPath = "/src/notes", IsActive = true };

	[Fact]
	public void RowMenu_SyncsAGitProjectWithOrigin_AndNonGitRowsHaveNoMenu()
	{
		var versionControl = new RecordingVersionControlService();
		using var context = CreateContext(versionControl, new FakeJobService(), Git(_api), NotGit(_notes));

		var view = context.Render<ProjectsView>();

		Assert.Empty(view.FindAll("button[aria-label='Notes actions']"));
		var sync = view.FindAll(".list-group button.menu-item").Single(button => button.TextContent.Trim() == "Sync with origin");
		Assert.False(sync.HasAttribute("disabled"));

		sync.Click();

		view.WaitForAssertion(() => Assert.Equal(["/src/api"], versionControl.Syncs));
		var toast = Assert.Single(context.Services.GetRequiredService<NotificationService>().Notifications);
		Assert.Equal(NotificationType.Success, toast.Type);
		Assert.Contains("Synced with origin/main.", toast.Message);
	}

	[Fact]
	public void RowMenu_SyncIsDisabledWhileAJobUsesTheCheckout()
	{
		var versionControl = new RecordingVersionControlService();
		var jobs = new FakeJobService { ActiveJobs = [new JobSummary { Id = Guid.NewGuid(), ProjectId = _api.Id, Status = JobStatus.Processing }] };
		using var context = CreateContext(versionControl, jobs, Git(_api));

		var view = context.Render<ProjectsView>();

		var sync = view.FindAll(".list-group button.menu-item").Single(button => button.TextContent.Trim() == "Sync with origin");
		Assert.True(sync.HasAttribute("disabled"));
		Assert.Contains("A job is using this checkout.", view.Find(".list-group .menu").TextContent);
	}

	[Fact]
	public void SyncAll_ConfirmsThenSyncsEachIdleGitProject_SkippingOnesAJobIsUsing()
	{
		var versionControl = new RecordingVersionControlService();
		var jobs = new FakeJobService();
		using var context = CreateContext(versionControl, jobs, Git(_api), Git(_web), NotGit(_notes));

		var view = context.Render<ProjectsView>();
		// A job starts on Web after the list loaded; the sync re-reads active jobs and leaves it alone.
		jobs.ActiveJobs = [new JobSummary { Id = Guid.NewGuid(), ProjectId = _web.Id, Status = JobStatus.Started }];

		view.FindAll("button.menu-item").Single(button => button.TextContent.Trim() == "Sync all projects with origin").Click();

		Assert.Empty(versionControl.Syncs);
		Assert.Contains("Sync 2 git projects with origin?", view.Find("dialog").TextContent);

		view.FindAll("dialog button").Single(button => button.TextContent.Trim() == "Sync all").Click();

		view.WaitForAssertion(() => Assert.Empty(view.FindAll("dialog")));
		Assert.Equal(["/src/api"], versionControl.Syncs);
		var toast = Assert.Single(context.Services.GetRequiredService<NotificationService>().Notifications);
		Assert.Equal(NotificationType.Success, toast.Type);
		Assert.Equal("Synced 1 of 1 project with origin. Skipped Web: a job is using the checkout.", toast.Message);
	}

	[Fact]
	public void SyncAll_ReportsEachFailureAndKeepsGoing()
	{
		var versionControl = new RecordingVersionControlService { FailingPaths = ["/src/api"] };
		using var context = CreateContext(versionControl, new FakeJobService(), Git(_api), Git(_web));

		var view = context.Render<ProjectsView>();
		view.FindAll("button.menu-item").Single(button => button.TextContent.Trim() == "Sync all projects with origin").Click();
		view.FindAll("dialog button").Single(button => button.TextContent.Trim() == "Sync all").Click();

		view.WaitForAssertion(() => Assert.Empty(view.FindAll("dialog")));
		Assert.Equal(["/src/api", "/src/web"], versionControl.Syncs);
		var notifications = context.Services.GetRequiredService<NotificationService>().Notifications;
		Assert.Contains(notifications, toast => toast.Type == NotificationType.Error && toast.Message == "Failed to fetch: offline");
		Assert.Contains(notifications, toast => toast.Type == NotificationType.Warning
			&& toast.Message == "Synced 1 of 2 projects with origin. 1 failed.");
	}

	[Fact]
	public void SyncAll_SyncsNothing_WhenActiveJobsCannotBeRead()
	{
		var versionControl = new RecordingVersionControlService();
		var jobs = new FakeJobService();
		using var context = CreateContext(versionControl, jobs, Git(_api));

		var view = context.Render<ProjectsView>();
		jobs.Error = new HttpRequestException("server unreachable");
		view.FindAll("button.menu-item").Single(button => button.TextContent.Trim() == "Sync all projects with origin").Click();
		view.FindAll("dialog button").Single(button => button.TextContent.Trim() == "Sync all").Click();

		view.WaitForAssertion(() => Assert.Contains("nothing was synced", view.Find("dialog").TextContent));
		Assert.Empty(versionControl.Syncs);
	}

	private static ProjectWithStats Git(Project project) => new() { Project = project, CurrentBranch = "main" };

	private static ProjectWithStats NotGit(Project project) => new() { Project = project };

	private static BunitContext CreateContext(RecordingVersionControlService versionControl, FakeJobService jobs, params ProjectWithStats[] projects)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProjectService>(new FakeProjectService(projects));
		context.Services.AddSingleton<IJobService>(jobs);
		context.Services.AddSingleton<IVersionControlService>(versionControl);
		context.Services.AddSingleton<NotificationService>();
		// The import dialog is held by @ref, so it renders for real; it calls nothing until opened.
		context.Services.AddSingleton(DispatchProxy.Create<IFileSystemService, UnusedService>());
		context.Services.AddSingleton(DispatchProxy.Create<ISettingsService, UnusedService>());
		context.ComponentFactories.AddStub<ProjectModal>();
		return context;
	}

	private sealed class FakeProjectService(ProjectWithStats[] projects) : FakeProjectServiceBase
	{
		public override Task<IEnumerable<ProjectFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<ProjectFolder>>([]);

		public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<ProjectWithStats>>(projects);
	}

	private sealed class FakeJobService : FakeJobServiceBase
	{
		public List<JobSummary> ActiveJobs { get; set; } = [];
		public Exception? Error { get; set; }

		public override Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
			=> Error != null ? Task.FromException<IEnumerable<JobSummary>>(Error) : Task.FromResult<IEnumerable<JobSummary>>(ActiveJobs);
	}

	private sealed class RecordingVersionControlService : FakeVersionControlServiceBase
	{
		public List<string> Syncs { get; } = [];
		public HashSet<string> FailingPaths { get; init; } = [];

		public override Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken cancellationToken = default)
		{
			Syncs.Add(workingDirectory);
			return Task.FromResult(FailingPaths.Contains(workingDirectory)
				? GitOperationResult.Failed("Failed to fetch: offline")
				: GitOperationResult.Succeeded(branchName: "main", remoteName: remoteName));
		}
	}

	public class UnusedService : DispatchProxy
	{
		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
			=> throw new NotSupportedException(targetMethod?.Name);
	}
}
