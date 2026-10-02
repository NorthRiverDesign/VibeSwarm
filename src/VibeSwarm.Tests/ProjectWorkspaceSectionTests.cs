using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// The new-project form is supposed to answer for itself what it can. These cover the
/// visible result of that: detection feedback, repository suggestions, and pre-filled fields.
/// </summary>
public sealed class ProjectWorkspaceSectionTests
{
	/// <summary>The workspace inspection debounces, so waits need headroom on a loaded runner.</summary>
	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

	[Fact]
	public void TypingAGitRepositoryPath_ShowsDetectionFeedback()
	{
		var fileSystem = new StubFileSystemService
		{
			Inspection = new WorkspaceInspection
			{
				Path = "/srv/code/api",
				Exists = true,
				IsGitRepository = true,
				GitHubRepository = "acme/api",
				CurrentBranch = "develop",
				DetectedStack = ".NET",
				SuggestedName = "api"
			}
		};

		using var context = CreateContext(fileSystem);
		var model = new ProjectModalFormModel();
		var cut = RenderSection(context, model);

		cut.Find("#modal-workingPath").Input("/srv/code/api");

		cut.WaitForAssertion(() =>
		{
			Assert.Contains("Git repository detected", cut.Markup);
			Assert.Contains("acme/api", cut.Markup);
			Assert.Contains("develop", cut.Markup);
			Assert.Contains(".NET", cut.Markup);
		}, WaitTimeout);
	}

	[Fact]
	public void TypingAPathThatDoesNotExist_SaysItWillBeCreated()
	{
		var fileSystem = new StubFileSystemService
		{
			Inspection = new WorkspaceInspection { Path = "/srv/code/new", SuggestedName = "new" }
		};

		using var context = CreateContext(fileSystem);
		var cut = RenderSection(context, new ProjectModalFormModel());

		cut.Find("#modal-workingPath").Input("/srv/code/new");

		cut.WaitForAssertion(() => Assert.Contains("This folder will be created", cut.Markup), WaitTimeout);
	}

	[Fact]
	public void PathAlreadyClaimedByAnotherProject_WarnsInsteadOfDetecting()
	{
		var fileSystem = new StubFileSystemService
		{
			Inspection = new WorkspaceInspection
			{
				Path = "/srv/code/api",
				Exists = true,
				IsGitRepository = true,
				SuggestedName = "api"
			}
		};

		using var context = CreateContext(fileSystem);
		var cut = RenderSection(context, new ProjectModalFormModel(), existingPaths: new Dictionary<string, string>
		{
			["/srv/code/api"] = "Existing API"
		});

		cut.Find("#modal-workingPath").Input("/srv/code/api");

		cut.WaitForAssertion(() =>
		{
			Assert.Contains("Existing API", cut.Markup);
			Assert.Contains("already uses this folder", cut.Markup);
		}, WaitTimeout);
	}

	[Fact]
	public void RepositoriesFoundOnHost_AreOfferedAndFillThePathWhenChosen()
	{
		var fileSystem = new StubFileSystemService
		{
			Scanned =
			[
				new WorkspaceInspection
				{
					Path = "/srv/code/api",
					Exists = true,
					IsGitRepository = true,
					GitHubRepository = "acme/api",
					SuggestedName = "api"
				},
				// Not a repository, so it should not be offered.
				new WorkspaceInspection { Path = "/srv/code/notes", Exists = true, SuggestedName = "notes" }
			]
		};

		using var context = CreateContext(fileSystem);
		var model = new ProjectModalFormModel();
		var cut = RenderSection(context, model, defaultProjectsDirectory: "/srv/code");

		cut.WaitForAssertion(() => Assert.Contains("Repositories found on this host", cut.Markup), WaitTimeout);
		Assert.DoesNotContain("notes", cut.Markup);

		cut.FindAll("button").Single(button => button.TextContent.Contains("acme/api", StringComparison.Ordinal)).Click();

		Assert.Equal("/srv/code/api", model.WorkingPath);
	}

	[Fact]
	public void RepositoriesAlreadyImported_AreNotOfferedAgain()
	{
		var fileSystem = new StubFileSystemService
		{
			Scanned =
			[
				new WorkspaceInspection
				{
					Path = "/srv/code/api",
					Exists = true,
					IsGitRepository = true,
					SuggestedName = "api"
				}
			]
		};

		using var context = CreateContext(fileSystem);
		var cut = RenderSection(context, new ProjectModalFormModel(),
			defaultProjectsDirectory: "/srv/code",
			existingPaths: new Dictionary<string, string> { ["/srv/code/api"] = "API" });

		Assert.DoesNotContain("Repositories found on this host", cut.Markup);
	}

	[Fact]
	public void EditingAProject_HidesTheSourcePickerAndSuggestions()
	{
		var fileSystem = new StubFileSystemService
		{
			Scanned =
			[
				new WorkspaceInspection { Path = "/srv/code/api", IsGitRepository = true, SuggestedName = "api" }
			]
		};

		using var context = CreateContext(fileSystem);
		var cut = RenderSection(context, new ProjectModalFormModel(), defaultProjectsDirectory: "/srv/code", isEdit: true);

		Assert.DoesNotContain("Start from", cut.Markup);
		Assert.DoesNotContain("Repositories found on this host", cut.Markup);
	}

	private static BunitContext CreateContext(IFileSystemService fileSystem)
	{
		var context = new BunitContext();
		context.Services.AddLogging();
		context.Services.AddSingleton(fileSystem);
		return context;
	}

	/// <summary>
	/// The section uses ValidationMessage, so it only renders inside an EditForm.
	/// </summary>
	private static IRenderedComponent<ProjectWorkspaceSection> RenderSection(
		BunitContext context,
		ProjectModalFormModel model,
		string? defaultProjectsDirectory = null,
		IReadOnlyDictionary<string, string>? existingPaths = null,
		bool isEdit = false)
	{
		var paths = existingPaths ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var host = context.Render(builder =>
		{
			builder.OpenComponent<EditForm>(0);
			builder.AddAttribute(1, nameof(EditForm.Model), model);
			builder.AddAttribute(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => child =>
			{
				child.OpenComponent<ProjectWorkspaceSection>(0);
				child.AddAttribute(1, nameof(ProjectWorkspaceSection.FormModel), model);
				child.AddAttribute(2, nameof(ProjectWorkspaceSection.FormState), new ProjectModalState());
				child.AddAttribute(3, nameof(ProjectWorkspaceSection.IsEdit), isEdit);
				child.AddAttribute(4, nameof(ProjectWorkspaceSection.DefaultProjectsDirectory), defaultProjectsDirectory);
				child.AddAttribute(5, nameof(ProjectWorkspaceSection.ExistingProjectPaths), paths);
				child.CloseComponent();
			}));
			builder.CloseComponent();
		});

		return host.FindComponent<ProjectWorkspaceSection>();
	}

	private sealed class StubFileSystemService : IFileSystemService
	{
		public WorkspaceInspection Inspection { get; set; } = new();

		public List<WorkspaceInspection> Scanned { get; set; } = [];

		public Task<DirectoryListResult> ListDirectoryAsync(string? path, bool directoriesOnly = false)
			=> Task.FromResult(new DirectoryListResult());

		public Task<bool> DirectoryExistsAsync(string path) => Task.FromResult(false);

		public Task<List<DriveEntry>> GetDrivesAsync() => Task.FromResult(new List<DriveEntry>());

		public Task<WorkspaceInspection> InspectWorkspaceAsync(string path) => Task.FromResult(Inspection);

		public Task<List<WorkspaceInspection>> ScanWorkspacesAsync(string rootPath) => Task.FromResult(Scanned);
	}
}
