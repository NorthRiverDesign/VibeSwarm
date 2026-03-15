using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Tests;

public sealed class ProjectModalStateTests
{
	[Fact]
	public void Validate_CloneModeWithOwnerOnlyRepository_ShowsRepositoryFieldError()
	{
		var project = new Project
		{
			Name = "Sample Project",
			WorkingPath = "/tmp/sample-project"
		};
		var state = new ProjectModalState();

		state.SelectSourceMode(project, ProjectCreationMode.CloneGitHubRepository);
		state.UpdateGitHubRepositoryInput(project, "sample-project");

		var isValid = state.Validate(project);

		Assert.False(isValid);
		Assert.Equal("GitHub repositories must use the format 'owner/repo'.", state.GitHubRepositoryError);
	}

	[Fact]
	public void BuildProjectCreationRequest_CreateModePreservesGitHubOptions()
	{
		var project = new Project
		{
			Name = "Sample Project",
			WorkingPath = "/tmp/sample-project"
		};
		var state = new ProjectModalState();

		state.SelectSourceMode(project, ProjectCreationMode.CreateGitHubRepository);
		state.UpdateGitHubRepositoryInput(project, "owner/sample-project");
		state.NewGitHubRepositoryDescription = "Managed by VibeSwarm";
		state.NewGitHubRepositoryPrivate = true;
		state.NewGitHubGitignoreTemplate = "CSharp";
		state.NewGitHubLicenseTemplate = "mit";
		state.NewGitHubInitializeReadme = true;

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
	public void TryMapSubmissionError_GitHubRepositoryMessage_AssignsFieldError()
	{
		var state = new ProjectModalState();

		var mapped = state.TryMapSubmissionError("GitHub repositories must use the format 'owner/repo'.");

		Assert.True(mapped);
		Assert.Equal("GitHub repositories must use the format 'owner/repo'.", state.GitHubRepositoryError);
	}

	[Fact]
	public void Validate_CreateModeWithTooLongRepositoryDescription_ShowsDescriptionFieldError()
	{
		var project = new Project
		{
			Name = "Sample Project",
			WorkingPath = "/tmp/sample-project"
		};
		var state = new ProjectModalState();

		state.SelectSourceMode(project, ProjectCreationMode.CreateGitHubRepository);
		state.UpdateGitHubRepositoryInput(project, "owner/sample-project");
		state.NewGitHubRepositoryDescription = new string('d', ValidationLimits.ProjectGitHubDescriptionMaxLength + 1);

		var isValid = state.Validate(project);

		Assert.False(isValid);
		Assert.Equal($"Repository description must be {ValidationLimits.ProjectGitHubDescriptionMaxLength:N0} characters or fewer.", state.GitHubRepositoryDescriptionError);
	}
}
