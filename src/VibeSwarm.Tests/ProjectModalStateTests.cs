using System.ComponentModel.DataAnnotations;
using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Validation;

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
}
