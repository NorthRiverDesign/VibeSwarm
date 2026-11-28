using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Models;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectModalTests
{
	/// <summary>The workspace inspection debounces, so waits need headroom on a loaded runner.</summary>
	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

[Fact]
public async Task RenderedProjectModal_UsesProjectBodyClassAndRendersRefactoredSections()
{
var provider = new Provider
{
Id = Guid.NewGuid(),
Name = "GitHub Copilot",
Type = ProviderType.Copilot,
IsEnabled = true
};

var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IProjectService>(new FakeProjectService());
services.AddSingleton<IProviderService>(new FakeProviderService(provider));
services.AddSingleton<ISettingsService>(new FakeSettingsService());
services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService([]));
services.AddSingleton<IFileSystemService>(new FakeWorkspaceFileSystemService());
services.AddSingleton<NotificationService>();
services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

var html = await renderer.Dispatcher.InvokeAsync(async () =>
{
var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
{
[nameof(ProjectModal.IsVisible)] = true
});

var output = await renderer.RenderComponentAsync<ProjectModal>(parameters);
return output.ToHtmlString();
});

Assert.Contains("vs-project-dialog-body", html);
Assert.Contains("vs-dialog-wide-lg", html);
Assert.Contains("dialog-lg", html);
// Essentials render up front; everything else lives in collapsed accordion sections.
Assert.Contains("Start from", html);
Assert.Contains("Working folder", html);
Assert.Contains("Project name", html);
Assert.Contains("accordion-item", html);
Assert.Contains("When a job finishes", html);
// Who does what is in view without opening anything.
Assert.Contains("Workflow", html);
Assert.Contains("Plan first", html);
Assert.DoesNotContain(">Agents<", html);
Assert.Contains(">Ideas<", html);
Assert.DoesNotContain("Providers and agents", html);
Assert.Contains("Instructions and memory", html);
Assert.Contains("Build verification", html);
Assert.Contains("Create Project", html);
}

[Fact]
public void SubmitCloneModeWithoutOwnerRepository_ShowsValidationMessage()
{
	using var context = CreateBunitContext();

var cut = context.Render<ProjectModal>(parameters => parameters
.Add(component => component.IsVisible, true));

cut.Find($"#project-source-{ProjectCreationMode.CloneGitHubRepository}").Change(true);
cut.Find("#modal-githubRepo").Input("sample-project");
cut.Find("form").Submit();

	Assert.Contains("GitHub repositories must use the format 'owner/repo'.", cut.Markup);
}

[Fact]
public void StickyActions_StayAboveFormContentScrollingUnderThem()
{
	using var context = CreateBunitContext();

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	// Open accordion items and focused input-groups raise their own z-index; without one of
	// its own the pinned Cancel/Save bar sat under them on a phone.
	var actions = cut.Find(".vs-dialog-sticky-actions");
	Assert.True(actions.ClassList.Contains("position-sticky"));
	Assert.True(actions.ClassList.Contains("z-sticky"));
	Assert.NotNull(actions.QuerySelector("button[type=submit]"));
}

[Fact]
public void BrowseGitHubRepositories_SelectingRepositoryPopulatesCloneInput()
{
	using var context = CreateBunitContext(new FakeProjectService
	{
		RepositoryBrowserResult = new GitHubRepositoryBrowserResult
		{
			IsGitHubCliAvailable = true,
			IsAuthenticated = true,
			Repositories =
			[
				new GitHubRepositoryBrowserItem
				{
					NameWithOwner = "octocat/hello-world",
					Description = "Sample repository"
				}
			]
		}
	});

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	cut.Find($"#project-source-{ProjectCreationMode.CloneGitHubRepository}").Change(true);
	cut.Find("#modal-githubRepoBrowse").Click();
	Assert.Empty(cut.FindAll("#githubRepositoryBrowserHideExisting"));
	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("octocat/hello-world", StringComparison.Ordinal))
		.Click();
	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Use Repository", StringComparison.Ordinal))
		.Click();

	Assert.Equal("octocat/hello-world", cut.Find("#modal-githubRepo").GetAttribute("value"));
}

[Fact]
public void BrowseGitHubRepositories_NullRepositoryListShowsEmptyState()
{
	using var context = CreateBunitContext(new FakeProjectService
	{
		RepositoryBrowserResult = new GitHubRepositoryBrowserResult
		{
			IsGitHubCliAvailable = true,
			IsAuthenticated = true,
			Repositories = null!
		}
	});

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	cut.Find($"#project-source-{ProjectCreationMode.CloneGitHubRepository}").Change(true);
	cut.Find("#modal-githubRepoBrowse").Click();

	Assert.Contains("No repositories matched the current filter.", cut.Markup);
}

[Fact]
public void BrowseGitHubRepositories_HidesRepositoriesAlreadyAddedUntilSwitchedOff()
{
	using var context = CreateBunitContext(new FakeProjectService
	{
		AllProjects =
		[
			new Project { Id = Guid.NewGuid(), Name = "Hello", WorkingPath = "/repos/hello", GitHubRepository = "Octocat/Hello-World.git" }
		],
		RepositoryBrowserResult = new GitHubRepositoryBrowserResult
		{
			IsGitHubCliAvailable = true,
			IsAuthenticated = true,
			Repositories =
			[
				new GitHubRepositoryBrowserItem { NameWithOwner = "octocat/hello-world" },
				new GitHubRepositoryBrowserItem { NameWithOwner = "octocat/spoon-knife" }
			]
		}
	});

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	cut.Find($"#project-source-{ProjectCreationMode.CloneGitHubRepository}").Change(true);
	cut.Find("#modal-githubRepoBrowse").Click();

	Assert.True(cut.Find("#githubRepositoryBrowserHideExisting").HasAttribute("checked"));
	Assert.Contains("Hide the 1 already in VibeSwarm", cut.Markup);
	Assert.Contains("spoon-knife", cut.Markup);
	Assert.DoesNotContain("hello-world", cut.Markup);

	cut.Find("#githubRepositoryBrowserHideExisting").Change(false);

	Assert.Contains("hello-world", cut.Markup);
	Assert.Contains("Added as Hello", cut.Markup);
}

[Fact]
public void BrowseGitHubRepositories_AllAlreadyAddedOffersToShowThem()
{
	using var context = CreateBunitContext(new FakeProjectService
	{
		AllProjects =
		[
			new Project { Id = Guid.NewGuid(), Name = "Hello", WorkingPath = "/repos/hello", GitHubRepository = "octocat/hello-world" }
		],
		RepositoryBrowserResult = new GitHubRepositoryBrowserResult
		{
			IsGitHubCliAvailable = true,
			IsAuthenticated = true,
			Repositories = [new GitHubRepositoryBrowserItem { NameWithOwner = "octocat/hello-world" }]
		}
	});

	var cut = context.Render<ProjectModal>(parameters => parameters
		.Add(component => component.IsVisible, true));

	cut.Find($"#project-source-{ProjectCreationMode.CloneGitHubRepository}").Change(true);
	cut.Find("#modal-githubRepoBrowse").Click();

	Assert.Contains("Every repository here is already in VibeSwarm.", cut.Markup);
	cut.FindAll("button")
		.Single(button => button.TextContent.Contains("Show them", StringComparison.Ordinal))
		.Click();

	Assert.Contains("hello-world", cut.Markup);
	Assert.False(cut.Find("#githubRepositoryBrowserHideExisting").HasAttribute("checked"));
}

	[Fact]
	public void EditProject_ShowsTheProfilesThatSuggestIdeasAndWriteCommitMessages()
	{
		var ollama = new InferenceProvider
		{
			Id = Guid.NewGuid(),
			Name = "Local Ollama",
			ProviderType = VibeSwarm.Shared.Inference.InferenceProviderType.Ollama,
			Endpoint = "http://localhost:11434",
			IsEnabled = true
		};
		var writer = new Profile { Id = Guid.NewGuid(), Name = "Commit writer", InferenceProviderId = ollama.Id, InferenceProvider = ollama, ModelId = "qwen3" };
		var coder = new Provider { Id = Guid.NewGuid(), Name = "GitHub Copilot", Type = ProviderType.Copilot, IsEnabled = true };
		var coding = new Profile { Id = Guid.NewGuid(), Name = "GitHub Copilot", ProviderId = coder.Id, Provider = coder, IsProviderDefault = true };

		using var context = CreateBunitContext(provider: coder, profiles: [coding, writer]);

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProject, new Project
			{
				Id = Guid.NewGuid(),
				Name = "Sample Project",
				WorkingPath = "/tmp/sample",
				CommitSummaryProfileId = writer.Id,
				CommitSummaryInferenceProviderId = ollama.Id,
				CommitSummaryInferenceModelId = "qwen3"
			}));

		Assert.Contains("Commit message writer", cut.Markup);
		Assert.Equal(writer.Id.ToString(), cut.Find("#modal-commitSummaryProfile").GetAttribute("value"));
		// Only inference profiles are offered, with the coding provider as the other choice.
		Assert.Equal(["The coding provider", "Commit writer"], cut.FindAll("#modal-commitSummaryProfile option").Select(option => option.TextContent));
		Assert.Equal(["The coding provider", "Commit writer"], cut.FindAll("#modal-ideasProfile option").Select(option => option.TextContent));
		Assert.Empty(cut.FindAll("#modal-commitSummaryInferenceProvider, #modal-commitSummaryInferenceModel, #modal-ideaInferenceProvider, #modal-ideaInferenceModel"));
	}

	[Fact]
	public void EditProject_ShowsItsRolesInTheWorkflowList_AndANewProjectBuildsWithTheDefaultProfile()
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "GitHub Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true,
			IsDefault = true
		};
		var providerDefault = new Profile { Id = Guid.NewGuid(), Name = "GitHub Copilot", ProviderId = provider.Id, Provider = provider, IsProviderDefault = true };
		var planner = new Profile { Id = Guid.NewGuid(), Name = "Planner", ProviderId = provider.Id, Provider = provider, ReasoningEffort = "medium" };
		var projectService = new FakeProjectService();

		using var context = CreateBunitContext(projectService, provider: provider, profiles: [providerDefault, planner]);

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProject, new Project
			{
				Id = Guid.NewGuid(),
				Name = "Sample Project",
				WorkingPath = "/tmp/sample",
				PlanningEnabled = true,
				PlanningProviderId = provider.Id,
				PlanningReasoningEffort = "medium",
				PlanningProfileId = planner.Id
			}));

		// The planner is named on its row; nothing asks for a provider, model or reasoning.
		var planRow = cut.FindAll("button[aria-expanded]").Single(row => row.TextContent.TrimStart().StartsWith("Plan first", StringComparison.Ordinal));
		Assert.Contains("Planner", planRow.TextContent);
		Assert.DoesNotContain("Off", planRow.TextContent);
		Assert.Empty(cut.FindAll("#modal-planningReasoning, #modal-planningProvider, #modal-planningModel"));

		var fresh = context.Render<ProjectModal>(parameters => parameters.Add(component => component.IsVisible, true));
		var buildRow = fresh.FindAll("button[aria-expanded]").Single(row => row.TextContent.TrimStart().StartsWith("Build", StringComparison.Ordinal));
		Assert.Contains("GitHub Copilot", buildRow.TextContent);
		buildRow.Click();
		Assert.Equal(providerDefault.Id.ToString(), fresh.Find("#workflow-build").GetAttribute("value"));
	}

	[Fact]
	public void EditProject_ClearProjectMemoryConfirmationClearsMemoryField()
	{
		using var context = CreateBunitContext();

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProject, new Project
			{
				Id = Guid.NewGuid(),
				Name = "Sample Project",
				WorkingPath = "/tmp/sample",
				Memory = "Remember the deployment checklist."
			}));

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Clear", StringComparison.Ordinal))
			.Click();

		Assert.Contains("Clear project memory", cut.Markup, StringComparison.OrdinalIgnoreCase);

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Clear memory", StringComparison.OrdinalIgnoreCase))
			.Click();

		Assert.True(string.IsNullOrEmpty(cut.Find("#modal-projectMemory").GetAttribute("value")));
		Assert.DoesNotContain(">Clear<", cut.Markup, StringComparison.Ordinal);
	}

	[Fact]
	public void CreateProject_UsesProjectNameForSuccessNotificationTitle()
	{
		var projectService = new FakeProjectService
		{
			CreateProjectResult = new Project
			{
				Id = Guid.NewGuid(),
				Name = "Demo Project",
				WorkingPath = "/tmp/demo-project"
			}
		};

		using var context = CreateBunitContext(projectService);
		var notificationService = context.Services.GetRequiredService<NotificationService>();

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true));

		cut.Find("#modal-name").Change("  Demo Project  ");
		cut.Find("#modal-workingPath").Input("/tmp/demo-project");
		cut.Find("form").Submit();

		cut.WaitForAssertion(() =>
		{
			var notification = Assert.Single(notificationService.Notifications);
			Assert.Equal("Demo Project", notification.Title);
			Assert.Equal(NotificationType.Success, notification.Type);
		}, WaitTimeout);
	}


	[Fact]
	public void TypingARepositoryToClone_FillsInTheWholeRepositoryNameAsTheProjectName()
	{
		using var context = CreateBunitContext();

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true));

		cut.Find($"#project-source-{ProjectCreationMode.CloneGitHubRepository}").Change(true);
		const string repository = "octocat/hello-world";
		for (var length = 1; length <= repository.Length; length++)
		{
			cut.Find("#modal-githubRepo").Input(repository[..length]);
		}

		// A synchronous Input() only queues the event while the renderer is busy (such as with a
		// folder inspection after its debounce), so wait for the last keystroke to be handled.
		cut.WaitForAssertion(() =>
		{
			Assert.Equal("hello-world", cut.Find("#modal-name").GetAttribute("value"));
			Assert.Equal("/tmp/projects/hello-world", cut.Find("#modal-workingPath").GetAttribute("value"));
		}, WaitTimeout);
	}

	[Fact]
	public void CreateRepository_PickingAnOrganizationCreatesTheRepositoryUnderIt()
	{
		var projectService = new FakeProjectService
		{
			AccountsResult = new GitHubAccountsResult
			{
				IsGitHubCliAvailable = true,
				IsAuthenticated = true,
				AuthenticatedUser = "octocat",
				Organizations = ["acme"]
			}
		};
		using var context = CreateBunitContext(projectService);

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true));

		cut.Find($"#project-source-{ProjectCreationMode.CreateGitHubRepository}").Change(true);

		var owners = cut.FindAll("#modal-githubOwner option");
		Assert.Equal(["octocat (me)", "acme"], owners.Select(option => option.TextContent.Trim()));
		Assert.Equal("octocat", cut.Find("#modal-githubOwner").GetAttribute("value"));

		cut.Find("#modal-githubOwner").Change("acme");
		cut.Find("#modal-githubRepo").Input("tool");
		cut.Find("form").Submit();

		cut.WaitForAssertion(() => Assert.NotNull(projectService.LastCreationRequest), WaitTimeout);
		var request = projectService.LastCreationRequest!;
		Assert.Equal(ProjectCreationMode.CreateGitHubRepository, request.Mode);
		Assert.Equal("acme/tool", request.GitHub!.Repository);
		Assert.Equal("tool", request.Project.Name);
	}

	[Fact]
	public void OpenedToCloneIntoAFolder_StartsInCloneMode_AndCreatesTheProjectInThatFolder()
	{
		var shop = new ProjectFolder { Id = Guid.NewGuid(), Name = "Shop" };
		var projectService = new FakeProjectService { Folders = [shop] };
		using var context = CreateBunitContext(projectService);

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.InitialFolderId, shop.Id)
			.Add(component => component.InitialCreationMode, ProjectCreationMode.CloneGitHubRepository));

		Assert.Equal("New Project in Shop", cut.Find("dialog h2").TextContent.Trim());
		Assert.True(cut.Find($"#project-source-{ProjectCreationMode.CloneGitHubRepository}").HasAttribute("checked"));

		cut.Find("#modal-githubRepo").Input("octocat/hello-world");
		cut.Find("form").Submit();

		cut.WaitForAssertion(() => Assert.NotNull(projectService.LastCreationRequest), WaitTimeout);
		var request = projectService.LastCreationRequest!;
		Assert.Equal(ProjectCreationMode.CloneGitHubRepository, request.Mode);
		Assert.Equal("octocat/hello-world", request.GitHub!.Repository);
		Assert.Equal(shop.Id, request.Project.FolderId);
	}

	[Fact]
	public void CreateRepository_WithoutGitHubAccounts_HidesTheOwnerPicker()
	{
		using var context = CreateBunitContext();

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true));

		cut.Find($"#project-source-{ProjectCreationMode.CreateGitHubRepository}").Change(true);

		Assert.Empty(cut.FindAll("#modal-githubOwner"));
		Assert.Contains("for an organization", cut.Markup);
	}

	[Fact]
	public void InspectingAGitWorkingFolder_FillsInNameRepositoryAndBuildCommands()
	{
		var fileSystem = new FakeWorkspaceFileSystemService
		{
			Inspection = new WorkspaceInspection
			{
				Path = "/srv/code/api",
				Exists = true,
				IsGitRepository = true,
				GitHubRepository = "acme/api",
				CurrentBranch = "main",
				SuggestedName = "api",
				DetectedStack = ".NET",
				SuggestedBuildCommand = "dotnet build",
				SuggestedTestCommand = "dotnet test"
			}
		};

		using var context = CreateBunitContext(fileSystemService: fileSystem);

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true));

		cut.Find("#modal-workingPath").Input("/srv/code/api");

		cut.WaitForAssertion(() =>
		{
			Assert.Equal("api", cut.Find("#modal-name").GetAttribute("value"));
			Assert.Contains("Git repository detected", cut.Markup);
		}, WaitTimeout);

		// Build commands are pre-filled inside the collapsed section, but verification stays opt-in.
		Assert.Equal("dotnet build", cut.Find("#modal-buildCommand").GetAttribute("value"));
		Assert.Equal("dotnet test", cut.Find("#modal-testCommand").GetAttribute("value"));
		Assert.Contains("Detected a <strong>.NET</strong> project", cut.Markup);
		Assert.False(cut.Find("#modal-buildVerificationEnabled").HasAttribute("checked"));
	}

	[Fact]
	public void InspectingAWorkingFolder_DoesNotOverwriteWhatTheUserAlreadyTyped()
	{
		var fileSystem = new FakeWorkspaceFileSystemService
		{
			Inspection = new WorkspaceInspection
			{
				Path = "/srv/code/api",
				Exists = true,
				IsGitRepository = true,
				SuggestedName = "api"
			}
		};

		using var context = CreateBunitContext(fileSystemService: fileSystem);

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true));

		cut.Find("#modal-name").Change("My Own Name");
		cut.Find("#modal-workingPath").Input("/srv/code/api");

		cut.WaitForAssertion(() => Assert.Contains("Git repository detected", cut.Markup), WaitTimeout);
		Assert.Equal("My Own Name", cut.Find("#modal-name").GetAttribute("value"));
	}


	[Fact]
	public void SubmittingWithAnErrorInsideACollapsedSection_OpensThatSection()
	{
		using var context = CreateBunitContext();

		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true));

		cut.Find("#modal-name").Change("Demo");
		cut.Find("#modal-workingPath").Input("/tmp/demo");

		// Build verification lives in a collapsed section and requires a build command.
		Assert.False(cut.FindAll("details.accordion-item").Last().HasAttribute("open"));
		cut.Find("#modal-buildVerificationEnabled").Change(true);
		cut.Find("form").Submit();

		cut.WaitForAssertion(() =>
		{
			Assert.Contains("Build command is required when build verification is enabled.", cut.Markup);
			Assert.True(cut.FindAll("details.accordion-item").Last().HasAttribute("open"));
		}, WaitTimeout);
	}

	[Fact]
	public void EditProject_PicksFolderAndSavesLinksSeparately()
	{
		var folder = new ProjectFolder { Id = Guid.NewGuid(), Name = "Storefront" };
		var project = new Project { Id = Guid.NewGuid(), Name = "Web", WorkingPath = "/tmp/web", FolderId = folder.Id };
		var sameFolder = new Project { Id = Guid.NewGuid(), Name = "Zeta Api", WorkingPath = "/tmp/api", FolderId = folder.Id };
		var elsewhere = new Project { Id = Guid.NewGuid(), Name = "Alpha Docs", WorkingPath = "/tmp/docs" };
		project.Links = [new ProjectLink { ProjectId = project.Id, LinkedProjectId = sameFolder.Id }];
		var projectService = new FakeProjectService
		{
			AllProjects = [project, sameFolder, elsewhere],
			Folders = [folder]
		};

		using var context = CreateBunitContext(projectService);
		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProject, project));

		Assert.Equal(folder.Id.ToString(), cut.Find("#modal-projectFolder").GetAttribute("value"));
		Assert.Empty(cut.FindAll($"#modal-linkedProject-{project.Id}"));
		var checkboxes = cut.FindAll("input[id^='modal-linkedProject-']");
		Assert.Equal([$"modal-linkedProject-{sameFolder.Id}", $"modal-linkedProject-{elsewhere.Id}"], checkboxes.Select(box => box.Id));
		Assert.True(checkboxes[0].HasAttribute("checked"));
		Assert.False(checkboxes[1].HasAttribute("checked"));

		cut.Find($"#modal-linkedProject-{sameFolder.Id}").Change(false);
		cut.Find($"#modal-linkedProject-{elsewhere.Id}").Change(true);
		cut.Find("#modal-projectFolder").Change(string.Empty);
		cut.Find("form").Submit();

		cut.WaitForAssertion(() =>
		{
			var update = Assert.Single(projectService.LinkUpdates);
			Assert.Equal(project.Id, update.ProjectId);
			Assert.Equal([elsewhere.Id], update.LinkedProjectIds);
			Assert.Null(projectService.LastUpdatedProject!.FolderId);
		}, WaitTimeout);
	}

	[Fact]
	public void EditProject_KeepsProfileLinks_WhenSavedUnchanged()
	{
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		var profileId = Guid.NewGuid();
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Linked",
			WorkingPath = "/tmp/linked",
			PlanningProfileId = profileId,
			ReviewProfileId = profileId,
			LeadProfileId = profileId,
			LeadProviderId = provider.Id,
			ProviderSelections = [new ProjectProvider { ProviderId = provider.Id, ProfileId = profileId, Priority = 0, IsEnabled = true }]
		};
		var projectService = new FakeProjectService { AllProjects = [project] };

		using var context = CreateBunitContext(projectService, provider: provider);
		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProject, project));
		cut.Find("form").Submit();

		cut.WaitForAssertion(() => Assert.NotNull(projectService.LastUpdatedProject), WaitTimeout);
		var saved = projectService.LastUpdatedProject!;
		Assert.Equal((profileId, profileId, profileId), (saved.PlanningProfileId!.Value, saved.ReviewProfileId!.Value, saved.LeadProfileId!.Value));
		Assert.Equal(profileId, Assert.Single(saved.ProviderSelections).ProfileId);
	}

	[Fact]
	public void EditProject_LeavesLinksAlone_WhenTheyDidNotChange()
	{
		var project = new Project { Id = Guid.NewGuid(), Name = "Web", WorkingPath = "/tmp/web" };
		var projectService = new FakeProjectService { AllProjects = [project] };

		using var context = CreateBunitContext(projectService);
		var cut = context.Render<ProjectModal>(parameters => parameters
			.Add(component => component.IsVisible, true)
			.Add(component => component.EditProject, project));

		Assert.Contains("There are no other projects to link yet.", cut.Markup);
		cut.Find("form").Submit();

		cut.WaitForAssertion(() => Assert.NotNull(projectService.LastUpdatedProject), WaitTimeout);
		Assert.Empty(projectService.LinkUpdates);
	}

private static BunitContext CreateBunitContext(
	FakeProjectService? projectService = null,
	Provider? provider = null,
	IReadOnlyList<InferenceProvider>? inferenceProviders = null,
	IFileSystemService? fileSystemService = null,
	IReadOnlyList<Profile>? profiles = null)
{
	var context = new BunitContext();
	context.JSInterop.SetupModule(ModalDialog.InteropModulePath).Mode = JSRuntimeMode.Loose;
	context.JSInterop.SetupVoid("vibeSwarmInitTouchDrag", _ => true).SetVoidResult();
	context.Services.AddLogging();
	var resolvedProvider = provider ?? new Provider
	{
		Id = Guid.NewGuid(),
		Name = "GitHub Copilot",
		Type = ProviderType.Copilot,
		IsEnabled = true
	};
	context.Services.AddSingleton<IProjectService>(projectService ?? new FakeProjectService());
	context.Services.AddSingleton<IProviderService>(new FakeProviderService(resolvedProvider));
	context.Services.AddSingleton<ISettingsService>(new FakeSettingsService());
	context.Services.AddSingleton<IInferenceProviderService>(new FakeInferenceProviderService(inferenceProviders ?? []));
	context.Services.AddSingleton<IFileSystemService>(fileSystemService ?? new FakeWorkspaceFileSystemService());
	context.Services.AddSingleton<NotificationService>();
	if (profiles != null)
	{
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(profiles));
	}

	return context;
}

private sealed class FakeProfileService(IReadOnlyList<Profile> profiles) : IProfileService
{
	public Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Profile>>(profiles);
	public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));
	public Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
	public Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
	public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

private sealed class FakeProjectService : FakeProjectServiceBase
{
public GitHubRepositoryBrowserResult RepositoryBrowserResult { get; set; } = new();
public GitHubAccountsResult? AccountsResult { get; set; }
public ProjectCreationRequest? LastCreationRequest { get; private set; }
public Project? CreateProjectResult { get; set; }
public Project? UpdateResult { get; set; }
public List<Project> AllProjects { get; set; } = [];
public List<ProjectFolder> Folders { get; set; } = [];
public Project? LastUpdatedProject { get; private set; }
public List<(Guid ProjectId, List<Guid> LinkedProjectIds)> LinkUpdates { get; } = [];
public override Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>(AllProjects);
public override Task<IEnumerable<ProjectFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectFolder>>(Folders);
public override Task SetLinkedProjectsAsync(Guid projectId, IEnumerable<Guid> linkedProjectIds, CancellationToken cancellationToken = default)
{
	LinkUpdates.Add((projectId, linkedProjectIds.ToList()));
	return Task.CompletedTask;
}
public override Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Project>>([]);
public override Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
public override Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(null);
public override Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default)
{
	LastCreationRequest = request;
	return Task.FromResult(CreateProjectResult ?? new Project
	{
		Id = Guid.NewGuid(),
		Name = request.Project.Name,
		WorkingPath = request.Project.WorkingPath
	});
}
public override Task<GitHubAccountsResult> GetGitHubAccountsAsync(CancellationToken cancellationToken = default)
	=> AccountsResult is null ? base.GetGitHubAccountsAsync(cancellationToken) : Task.FromResult(AccountsResult);
public override Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult(RepositoryBrowserResult);
public override Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default)
{
	LastUpdatedProject = project;
	return Task.FromResult(UpdateResult ?? project);
}
public override Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<ProjectWithStats>>([]);
public override Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardProjectInfo>>([]);
public override Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardJobMetrics { RangeDays = rangeDays, Buckets = [] });
public override Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<DashboardRunningJobInfo>>([]);
}

private sealed class FakeProviderService(Provider provider) : FakeProviderServiceBase
{
private readonly Provider _provider = provider;
public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Provider>>([_provider]);
public override Task<Provider?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(id == _provider.Id ? _provider : null);
public override Task<Provider?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Provider?>(_provider);
public override Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken cancellationToken = default)
=> Task.FromResult<IEnumerable<ProviderModel>>(
[
new ProviderModel
{
ProviderId = providerId,
ModelId = "gpt-5.4",
DisplayName = "GPT-5.4",
IsAvailable = true,
IsDefault = true
}
]);
}

private sealed class FakeSettingsService : ISettingsService
{
public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
public Task<AppSettings> UpdateMaxConcurrentJobsAsync(int maxConcurrentJobs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
public Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
public Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("/tmp/projects");
}

private sealed class NoOpJsRuntime : IJSRuntime
{
public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
=> ValueTask.FromResult(default(TValue)!);

public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
=> ValueTask.FromResult(default(TValue)!);
}

private sealed class FakeInferenceProviderService(IReadOnlyList<InferenceProvider> providers) : FakeInferenceProviderServiceBase
{
private readonly IReadOnlyList<InferenceProvider> _providers = providers;
public override Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>(_providers);
public override Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_providers.FirstOrDefault(provider => provider.Id == id));
public override Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceProvider>>(_providers.Where(provider => provider.IsEnabled));
public override Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>(_providers.FirstOrDefault(provider => provider.Id == providerId)?.Models ?? []);
public override Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<IEnumerable<InferenceModel>>([]);
public override Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default) => Task.FromResult<InferenceModel?>(null);
}

	private sealed class FakeWorkspaceFileSystemService : IFileSystemService
	{
		public WorkspaceInspection? Inspection { get; set; }

		public Task<DirectoryListResult> ListDirectoryAsync(string? path, bool directoriesOnly = false)
			=> Task.FromResult(new DirectoryListResult());

		public Task<bool> DirectoryExistsAsync(string path) => Task.FromResult(false);
		public Task<List<DriveEntry>> GetDrivesAsync() => Task.FromResult(new List<DriveEntry>());

		public Task<WorkspaceInspection> InspectWorkspaceAsync(string path)
			=> Task.FromResult(Inspection ?? new WorkspaceInspection { Path = path });

		public Task<List<WorkspaceInspection>> ScanWorkspacesAsync(string rootPath)
			=> Task.FromResult(new List<WorkspaceInspection>());
	}

}
