using System.ComponentModel.DataAnnotations;
using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectModalStateTests
{
[Fact]
public void ProjectModalFormModel_CloneModeWithOwnerOnlyRepository_FailsValidation()
{
var project = new ProjectModalFormModel
{
Name = "Sample Project",
WorkingPath = "/tmp/sample-project",
CreationMode = ProjectCreationMode.CloneGitHubRepository,
GitHubRepositoryInput = "sample-project"
};
var validationResults = new List<ValidationResult>();
var isValid = Validator.TryValidateObject(project, new ValidationContext(project), validationResults, validateAllProperties: true);

Assert.False(isValid);
Assert.Contains(validationResults, result => result.ErrorMessage == "GitHub repositories must use the format 'owner/repo'.");
}

[Fact]
public void BuildProjectCreationRequest_CreateModePreservesGitHubOptions()
{
var project = new ProjectModalFormModel
{
Name = "Sample Project",
WorkingPath = "/tmp/sample-project",
CreationMode = ProjectCreationMode.CreateGitHubRepository,
GitHubRepositoryInput = "owner/sample-project",
NewGitHubRepositoryDescription = "Managed by VibeSwarm",
NewGitHubRepositoryPrivate = true,
NewGitHubGitignoreTemplate = "CSharp",
NewGitHubLicenseTemplate = "mit",
NewGitHubInitializeReadme = true
};
var state = new ProjectModalState();

var request = state.BuildProjectCreationRequest(project);

Assert.Equal(ProjectCreationMode.CreateGitHubRepository, request.Mode);
Assert.NotNull(request.GitHub);
Assert.Equal("owner/sample-project", request.GitHub!.Repository);
Assert.Equal("Managed by VibeSwarm", request.GitHub.Description);
Assert.True(request.GitHub.IsPrivate);
Assert.Equal("CSharp", request.GitHub.GitignoreTemplate);
Assert.Equal("mit", request.GitHub.LicenseTemplate);
Assert.True(request.GitHub.InitializeReadme);
}

[Fact]
public void ProjectModalFormModel_CreateModeWithTooLongRepositoryDescription_FailsValidation()
{
var project = new ProjectModalFormModel
{
Name = "Sample Project",
WorkingPath = "/tmp/sample-project",
CreationMode = ProjectCreationMode.CreateGitHubRepository,
GitHubRepositoryInput = "owner/sample-project",
NewGitHubRepositoryDescription = new string('d', ValidationLimits.ProjectGitHubDescriptionMaxLength + 1)
};
var validationResults = new List<ValidationResult>();
var isValid = Validator.TryValidateObject(project, new ValidationContext(project), validationResults, validateAllProperties: true);

Assert.False(isValid);
Assert.Contains(validationResults, result => result.MemberNames.Contains(nameof(ProjectModalFormModel.NewGitHubRepositoryDescription)));
}

	[Fact]
	public void ProjectModalFormModel_BuildVerificationWithoutBuildCommand_FailsValidation()
	{
var project = new ProjectModalFormModel
{
Name = "Sample Project",
WorkingPath = "/tmp/sample-project",
BuildVerificationEnabled = true
};
var validationResults = new List<ValidationResult>();
var isValid = Validator.TryValidateObject(project, new ValidationContext(project), validationResults, validateAllProperties: true);

		Assert.False(isValid);
		Assert.Contains(validationResults, result => result.ErrorMessage == "Build command is required when build verification is enabled.");
	}

	[Fact]
	public void ProjectModalFormModel_TeamAssignmentWithoutProvider_FailsValidation()
	{
		var project = new ProjectModalFormModel
		{
			Name = "Sample Project",
			WorkingPath = "/tmp/sample-project",
			AgentAssignments =
			[
				new ProjectAgent
				{
					AgentId = Guid.NewGuid(),
					ProviderId = Guid.Empty
				}
			]
		};
		var validationResults = new List<ValidationResult>();
		var isValid = Validator.TryValidateObject(project, new ValidationContext(project), validationResults, validateAllProperties: true);

		Assert.False(isValid);
		Assert.Contains(validationResults, result => result.ErrorMessage == "Each assigned agent must have a provider.");
	}

	[Fact]
	public void UpdateGitHubRepositoryInput_NameFollowsTheRepositoryAsItIsTyped()
	{
		var project = new ProjectModalFormModel { CreationMode = ProjectCreationMode.CloneGitHubRepository };
		var state = new ProjectModalState();
		state.SetDefaultProjectsDirectory("/srv/code");

		const string repository = "acme/api-server";
		for (var length = 1; length <= repository.Length; length++)
		{
			state.UpdateGitHubRepositoryInput(project, repository[..length]);
		}

		Assert.Equal("api-server", project.Name);
		Assert.Equal(Path.Combine("/srv/code", "api-server"), project.WorkingPath);
	}

	[Fact]
	public void UpdateGitHubRepositoryInput_KeepsANameTheUserTyped()
	{
		var project = new ProjectModalFormModel { CreationMode = ProjectCreationMode.CloneGitHubRepository };
		var state = new ProjectModalState();

		state.UpdateGitHubRepositoryInput(project, "acme/a");
		project.Name = "My API";
		state.UpdateGitHubRepositoryInput(project, "acme/api");

		Assert.Equal("My API", project.Name);
	}

	[Fact]
	public void SuggestName_FolderSuggestionReplacesAnAutoFilledNameUntilReset()
	{
		var project = new ProjectModalFormModel { CreationMode = ProjectCreationMode.CloneGitHubRepository };
		var state = new ProjectModalState();

		state.UpdateGitHubRepositoryInput(project, "acme/a");
		state.SuggestName(project, "api");
		Assert.Equal("api", project.Name);

		// A fresh form forgets what was auto-filled, so a matching name now counts as typed.
		state.Reset();
		state.SuggestName(project, "other");
		Assert.Equal("api", project.Name);
	}

	[Fact]
	public void SetGitHubAccounts_DefaultsToTheUsersOwnAccount()
	{
		var project = new ProjectModalFormModel { CreationMode = ProjectCreationMode.CreateGitHubRepository };
		var state = new ProjectModalState();

		state.SetGitHubAccounts(project, Accounts("octocat", "acme", "zeta"));
		state.UpdateGitHubRepositoryInput(project, "my-project");

		Assert.Equal(["octocat", "acme", "zeta"], state.GitHubOwners);
		Assert.Equal("octocat", project.NewGitHubRepositoryOwner);
		Assert.Equal("octocat/my-project", state.BuildProjectCreationRequest(project).GitHub!.Repository);
	}

	[Fact]
	public void SelectGitHubOwner_ReplacesAnOwnerTypedIntoTheRepositoryName()
	{
		var project = new ProjectModalFormModel { CreationMode = ProjectCreationMode.CreateGitHubRepository };
		var state = new ProjectModalState();
		state.SetGitHubAccounts(project, Accounts("octocat", "acme"));
		state.UpdateGitHubRepositoryInput(project, "octocat/tool");

		state.SelectGitHubOwner(project, "acme");

		Assert.Equal("tool", project.GitHubRepositoryInput);
		Assert.Equal("acme/tool", project.GetNormalizedGitHubRepository());
	}

	[Fact]
	public void UpdateGitHubRepositoryInput_TypedOwnerWinsAndMovesThePicker()
	{
		var project = new ProjectModalFormModel { CreationMode = ProjectCreationMode.CreateGitHubRepository };
		var state = new ProjectModalState();
		state.SetGitHubAccounts(project, Accounts("octocat", "acme"));

		state.UpdateGitHubRepositoryInput(project, "ACME/tool");
		Assert.Equal("acme", project.NewGitHubRepositoryOwner);
		Assert.Equal("ACME/tool", project.GetNormalizedGitHubRepository());

		// An owner that isn't a listed account still shows in the picker rather than contradicting the name.
		state.UpdateGitHubRepositoryInput(project, "elsewhere/tool");
		Assert.Equal("elsewhere", project.NewGitHubRepositoryOwner);
		Assert.Equal(["octocat", "acme", "elsewhere"], state.GetGitHubOwnerOptions(project));
		Assert.Equal("elsewhere/tool", project.GetNormalizedGitHubRepository());
	}

	[Fact]
	public void SetGitHubAccounts_WhenTheGitHubCliFails_OffersNoOwners()
	{
		var project = new ProjectModalFormModel { CreationMode = ProjectCreationMode.CreateGitHubRepository };
		var state = new ProjectModalState();

		state.SetGitHubAccounts(project, new GitHubAccountsResult { ErrorMessage = "Sign in with 'gh auth login'." });
		state.UpdateGitHubRepositoryInput(project, "acme/tool");
		state.UpdateGitHubRepositoryInput(project, "my-project");

		Assert.Empty(state.GitHubOwners);
		Assert.Equal(string.Empty, project.NewGitHubRepositoryOwner);
		Assert.Equal("my-project", project.GetNormalizedGitHubRepository());
	}

	private static GitHubAccountsResult Accounts(string user, params string[] organizations) => new()
	{
		IsGitHubCliAvailable = true,
		IsAuthenticated = true,
		AuthenticatedUser = user,
		Organizations = [.. organizations]
	};
}
