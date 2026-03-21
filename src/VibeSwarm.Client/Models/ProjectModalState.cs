using VibeSwarm.Shared.Models;

namespace VibeSwarm.Client.Models;

public sealed class ProjectModalState
{
private string? _defaultProjectsDirectory;

public string WorkingPathPlaceholder => !string.IsNullOrEmpty(_defaultProjectsDirectory)
? Path.Combine(_defaultProjectsDirectory, "project-name")
: (OperatingSystem.IsWindows() ? @"C:\projects\myapp" : "/home/user/projects/myapp");

public void SetDefaultProjectsDirectory(string? defaultProjectsDirectory)
{
_defaultProjectsDirectory = defaultProjectsDirectory;
}

public void LoadFromProject(ProjectModalFormModel project)
{
project.GitHubRepositoryInput = project.GitHubRepository ?? string.Empty;
}

public void SelectSourceMode(ProjectModalFormModel project, ProjectCreationMode mode)
{
project.CreationMode = mode;

if (project.CreationMode == ProjectCreationMode.CreateGitHubRepository &&
string.IsNullOrWhiteSpace(project.GitHubRepositoryInput) &&
!string.IsNullOrWhiteSpace(project.Name))
{
project.GitHubRepositoryInput = SanitizeRepositoryName(project.Name);
}

ApplyRepositorySuggestions(project);
}

public void UpdateGitHubRepositoryInput(ProjectModalFormModel project, string? repositoryInput)
{
project.GitHubRepositoryInput = repositoryInput?.Trim() ?? string.Empty;
ApplyRepositorySuggestions(project);
}

public ProjectCreationRequest BuildProjectCreationRequest(ProjectModalFormModel project)
{
var request = new ProjectCreationRequest
{
Project = project.ToProject(),
Mode = project.CreationMode
};

if (project.CreationMode == ProjectCreationMode.CloneGitHubRepository)
{
request.GitHub = new GitHubRepositoryOptions
{
Repository = project.GetNormalizedGitHubRepository() ?? string.Empty
};
}
else if (project.CreationMode == ProjectCreationMode.CreateGitHubRepository)
{
request.GitHub = new GitHubRepositoryOptions
{
Repository = project.GetNormalizedGitHubRepository() ?? string.Empty,
Description = string.IsNullOrWhiteSpace(project.NewGitHubRepositoryDescription) ? null : project.NewGitHubRepositoryDescription.Trim(),
IsPrivate = project.NewGitHubRepositoryPrivate,
GitignoreTemplate = string.IsNullOrWhiteSpace(project.NewGitHubGitignoreTemplate) ? null : project.NewGitHubGitignoreTemplate,
LicenseTemplate = string.IsNullOrWhiteSpace(project.NewGitHubLicenseTemplate) ? null : project.NewGitHubLicenseTemplate,
InitializeReadme = project.NewGitHubInitializeReadme
};
}

return request;
}

public string GetSourceModeButtonClass(ProjectModalFormModel project, ProjectCreationMode mode)
=> project.CreationMode == mode ? "btn-primary" : "btn-secondary";

public string GetWorkingPathHelpText(ProjectModalFormModel project) => project.CreationMode switch
{
ProjectCreationMode.CloneGitHubRepository => "Repository contents will be cloned into this directory when the project is created.",
ProjectCreationMode.CreateGitHubRepository => "This directory will become the local workspace for the new GitHub repository.",
_ => "Directory where CLI providers execute commands."
};

public string GetProgressMessage(ProjectModalFormModel project) => project.CreationMode switch
{
ProjectCreationMode.CloneGitHubRepository => "Cloning GitHub repository...",
ProjectCreationMode.CreateGitHubRepository => "Creating GitHub repository...",
_ => "Creating project..."
};

public string GetSuccessMessage(ProjectModalFormModel project) => project.CreationMode switch
{
ProjectCreationMode.CloneGitHubRepository => "Project created and GitHub repository cloned successfully.",
ProjectCreationMode.CreateGitHubRepository => "Project created and GitHub repository linked successfully.",
_ => "Project created successfully."
};

public string GetSubmitButtonText(ProjectModalFormModel project, bool isSaving, bool isEdit)
{
if (isSaving)
{
return "Saving...";
}

if (isEdit)
{
return "Update Project";
}

return project.CreationMode switch
{
ProjectCreationMode.CloneGitHubRepository => "Clone & Create",
ProjectCreationMode.CreateGitHubRepository => "Create Repo & Project",
_ => "Create Project"
};
}

private void ApplyRepositorySuggestions(ProjectModalFormModel project)
{
var repositoryName = ExtractRepositoryName(project.GitHubRepositoryInput);
if (string.IsNullOrWhiteSpace(repositoryName))
{
return;
}

if (!string.IsNullOrEmpty(_defaultProjectsDirectory) &&
(string.IsNullOrWhiteSpace(project.WorkingPath) || project.WorkingPath.StartsWith(_defaultProjectsDirectory, StringComparison.Ordinal)))
{
project.WorkingPath = Path.Combine(_defaultProjectsDirectory, repositoryName);
}

if (string.IsNullOrWhiteSpace(project.Name))
{
project.Name = repositoryName;
}
}

private static string ExtractRepositoryName(string? value)
{
if (string.IsNullOrWhiteSpace(value))
{
return string.Empty;
}

var sanitized = value.Trim();
if (sanitized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
{
sanitized = sanitized[..^4];
}

sanitized = sanitized.Trim('/').Replace('\\', '/');
var segments = sanitized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
return segments.LastOrDefault() ?? string.Empty;
}

private static string SanitizeRepositoryName(string name)
{
var sanitized = string.Join(string.Empty, name.Select(c =>
char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-'));

while (sanitized.Contains("--", StringComparison.Ordinal))
{
sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
}

return sanitized.Trim('-').ToLowerInvariant();
}
}
