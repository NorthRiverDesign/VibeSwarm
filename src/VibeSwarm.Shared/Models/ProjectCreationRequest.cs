using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

public enum ProjectCreationMode
{
	ExistingDirectory = 0,
	CloneGitHubRepository = 1,
	CreateGitHubRepository = 2
}

public sealed class GitHubRepositoryOptions
{
	[StringLength(200)]
	public string? Repository { get; set; }

	[StringLength(500)]
	public string? Description { get; set; }

	public bool IsPrivate { get; set; }

	[StringLength(100)]
	public string? GitignoreTemplate { get; set; }

	[StringLength(100)]
	public string? LicenseTemplate { get; set; }

	public bool InitializeReadme { get; set; }
}

public sealed class ProjectCreationRequest
{
	[Required]
	public Project Project { get; set; } = new();

	public ProjectCreationMode Mode { get; set; } = ProjectCreationMode.ExistingDirectory;

	public GitHubRepositoryOptions? GitHub { get; set; }
}
