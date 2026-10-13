using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class AddProjectToFolderTests
{
	private readonly ProjectFolder _shop = new() { Id = Guid.NewGuid(), Name = "Shop" };
	private readonly ProjectFolder _tools = new() { Id = Guid.NewGuid(), Name = "Tools" };

	[Fact]
	public void FolderPage_Add_ListsProjectsOutsideTheFolder_AndMovesTheChosenOneIn()
	{
		var web = new Project { Id = Guid.NewGuid(), Name = "Web", FolderId = _shop.Id };
		var docs = new Project { Id = Guid.NewGuid(), Name = "Docs", FolderId = _tools.Id };
		var api = new Project { Id = Guid.NewGuid(), Name = "Api" };
		var projects = new FakeProjectService([_shop, _tools], web, docs, api);
		using var context = CreateContext(projects);

		var page = context.Render<ProjectFolderDetail>(parameters => parameters.Add(component => component.FolderId, _shop.Id));
		page.Find("button[aria-label='Add a project']").Click();

		var rows = page.FindAll("dialog .list-group-item-action");
		Assert.Equal(["Api", "Docs"], rows.Select(row => row.QuerySelector(".fw-medium")!.TextContent));
		Assert.Contains("No folder", rows[0].TextContent);
		Assert.Contains("In Tools", rows[1].TextContent);

		rows[1].Click();

		Assert.Equal([(docs.Id, (Guid?)_shop.Id)], projects.Moves);
		page.WaitForAssertion(() => Assert.Empty(page.FindAll("dialog")));
		Assert.Contains("Docs", page.Find(".list-group").TextContent);
		Assert.DoesNotContain("Api", page.Find(".list-group").TextContent);
	}

	[Fact]
	public void AddModal_ShowsTheError_AndStaysOpen_WhenTheMoveFails()
	{
		var api = new Project { Id = Guid.NewGuid(), Name = "Api" };
		var projects = new FakeProjectService([_shop], api) { MoveError = new InvalidOperationException("That folder was not found.") };
		using var context = CreateContext(projects);
		var moved = new List<Project>();

		var modal = context.Render<AddProjectToFolderModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.Folder, _shop)
			.Add(component => component.Projects, [api])
			.Add(component => component.Folders, [_shop])
			.Add(component => component.OnMoved, project => moved.Add(project)));

		modal.Find("dialog .list-group-item-action").Click();

		Assert.Contains("That folder was not found.", modal.Find(".alert").TextContent);
		Assert.NotNull(modal.Find("dialog"));
		Assert.Empty(moved);
		Assert.Null(api.FolderId);
	}

	[Fact]
	public void AddModal_SearchesByName_AndSaysWhenEveryProjectIsAlreadyInTheFolder()
	{
		var api = new Project { Id = Guid.NewGuid(), Name = "Api", WorkingPath = "/src/api" };
		var web = new Project { Id = Guid.NewGuid(), Name = "Web", WorkingPath = "/src/web", IsActive = false };
		using var context = CreateContext(new FakeProjectService([_shop], api, web));

		var modal = context.Render<AddProjectToFolderModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.Folder, _shop)
			.Add(component => component.Projects, [api, web])
			.Add(component => component.Folders, [_shop]));

		Assert.Contains("No folder · Inactive", modal.FindAll("dialog .list-group-item-action")[1].TextContent);
		modal.Find("#add-to-folder-search").Input("we");
		Assert.Equal("Web", modal.Find("dialog .list-group-item-action .fw-medium").TextContent);

		modal.Render(parameters => parameters.Add(component => component.Projects,
			[new Project { Id = api.Id, Name = "Api", FolderId = _shop.Id }]));

		Assert.Contains("Every project is already in this folder.", modal.Markup);
		Assert.Empty(modal.FindAll("dialog .list-group-item-action"));
	}

	[Fact]
	public void ProjectsList_FolderSectionAdd_MovesAProjectIntoThatFolder()
	{
		var api = new Project { Id = Guid.NewGuid(), Name = "Api", IsActive = true };
		var web = new Project { Id = Guid.NewGuid(), Name = "Web", IsActive = true, FolderId = _shop.Id };
		var projects = new FakeProjectService([_shop, _tools], api, web);
		using var context = CreateContext(projects);
		// The import dialog is held by @ref, so it renders for real; it calls nothing until opened.
		context.Services.AddSingleton(DispatchProxy.Create<IFileSystemService, UnusedService>());
		context.Services.AddSingleton(DispatchProxy.Create<ISettingsService, UnusedService>());
		context.ComponentFactories.AddStub<ProjectModal>();

		var view = context.Render<ProjectsView>();
		var toolsSection = view.FindAll("section").Single(section => section.QuerySelector("h2")!.TextContent == "Tools");
		toolsSection.QuerySelectorAll("button.btn-link").Single(button => button.TextContent == "Add").Click();

		Assert.Equal("Add to Tools", view.Find("dialog h2").TextContent.Trim());
		var rows = view.FindAll("dialog .list-group-item-action");
		Assert.Equal(["Api", "Web"], rows.Select(row => row.QuerySelector(".fw-medium")!.TextContent));

		rows[0].Click();

		Assert.Equal([(api.Id, (Guid?)_tools.Id)], projects.Moves);
		view.WaitForAssertion(() => Assert.Empty(view.FindAll("dialog")));
		var refreshedTools = view.FindAll("section").Single(section => section.QuerySelector("h2")!.TextContent == "Tools");
		Assert.Contains("Api", refreshedTools.TextContent);
	}

	private static BunitContext CreateContext(FakeProjectService projects)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProjectService>(projects);
		context.Services.AddSingleton<IJobService>(new FakeJobService());
		context.Services.AddSingleton<NotificationService>();
		return context;
	}

	private sealed class FakeProjectService(List<ProjectFolder> folders, params Project[] projects) : FakeProjectServiceBase
	{
		public List<(Guid ProjectId, Guid? FolderId)> Moves { get; } = [];
		public Exception? MoveError { get; init; }

		public override Task<IEnumerable<ProjectFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<ProjectFolder>>(folders);

		public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult(projects.Select(project => new ProjectWithStats { Project = project }));

		public override Task MoveToFolderAsync(Guid projectId, Guid? folderId, CancellationToken cancellationToken = default)
		{
			if (MoveError != null)
			{
				throw MoveError;
			}

			Moves.Add((projectId, folderId));
			projects.Single(project => project.Id == projectId).FolderId = folderId;
			return Task.CompletedTask;
		}
	}

	public class UnusedService : DispatchProxy
	{
		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
			=> throw new NotSupportedException(targetMethod?.Name);
	}

	private sealed class FakeJobService : FakeJobServiceBase
	{
		public override Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<JobSummary>>([]);
	}
}
