using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Client.Models;

public sealed class ProjectModalState
{
private string? _defaultProjectsDirectory;
private string? _autoFilledName;
private List<string> _gitHubOwners = [];

/// <summary>True while the owners for a new repository are being listed.</summary>
public bool IsLoadingGitHubAccounts { get; set; }

/// <summary>The GitHub CLI's signed-in user, once the accounts have loaded.</summary>
public string? GitHubUser { get; private set; }

/// <summary>Accounts a new repository can be created under: the user first, then organizations.</summary>
public IReadOnlyList<string> GitHubOwners => _gitHubOwners;

public string WorkingPathPlaceholder => !string.IsNullOrEmpty(_defaultProjectsDirectory)
? Path.Combine(_defaultProjectsDirectory, "project-name")
: (OperatingSystem.IsWindows() ? @"C:\projects\myapp" : "/home/user/projects/myapp");

public void SetDefaultProjectsDirectory(string? defaultProjectsDirectory)
{
_defaultProjectsDirectory = defaultProjectsDirectory;
}

/// <summary>Forgets everything learned about the previous form, for a freshly opened modal.</summary>
public void Reset()
{
_autoFilledName = null;
_gitHubOwners = [];
GitHubUser = null;
IsLoadingGitHubAccounts = false;
}

public void LoadFromProject(ProjectModalFormModel project)
{
project.GitHubRepositoryInput = project.GitHubRepository ?? string.Empty;
}

/// <summary>
/// Fills the project name from a repository or folder name. A name this method filled in
/// keeps following later suggestions (each keystroke of a repository name); one the user
/// typed is never overwritten.
/// </summary>
public void SuggestName(ProjectModalFormModel project, string? suggestedName)
{
if (string.IsNullOrWhiteSpace(suggestedName))
{
return;
}

if (string.IsNullOrWhiteSpace(project.Name) || string.Equals(project.Name, _autoFilledName, StringComparison.Ordinal))
{
project.Name = suggestedName;
_autoFilledName = suggestedName;
}
}

/// <summary>Offers the loaded accounts as owners for a new repository, defaulting to the user's own.</summary>
public void SetGitHubAccounts(ProjectModalFormModel project, GitHubAccountsResult accounts)
{
IsLoadingGitHubAccounts = false;
if (!string.IsNullOrWhiteSpace(accounts.ErrorMessage) || string.IsNullOrWhiteSpace(accounts.AuthenticatedUser))
{
GitHubUser = null;
_gitHubOwners = [];
return;
}

GitHubUser = accounts.AuthenticatedUser;
_gitHubOwners = [accounts.AuthenticatedUser, .. (accounts.Organizations ?? []).Where(owner => !string.IsNullOrWhiteSpace(owner))];

SyncOwnerFromRepositoryInput(project);
if (string.IsNullOrWhiteSpace(project.NewGitHubRepositoryOwner))
{
project.NewGitHubRepositoryOwner = accounts.AuthenticatedUser;
}
}

/// <summary>Owner choices, plus an owner typed into the repository field that isn't one of the loaded accounts.</summary>
public IReadOnlyList<string> GetGitHubOwnerOptions(ProjectModalFormModel project)
{
var selected = project.NewGitHubRepositoryOwner;
return string.IsNullOrWhiteSpace(selected) || _gitHubOwners.Contains(selected, StringComparer.OrdinalIgnoreCase)
? _gitHubOwners
: [.. _gitHubOwners, selected];
}

/// <summary>Picking an owner replaces any owner typed into the repository field.</summary>
public void SelectGitHubOwner(ProjectModalFormModel project, string? owner)
{
project.NewGitHubRepositoryOwner = owner?.Trim() ?? string.Empty;
if (TryGetTypedOwner(project.GitHubRepositoryInput, out _))
{
project.GitHubRepositoryInput = ExtractRepositoryName(project.GitHubRepositoryInput);
}
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

SyncOwnerFromRepositoryInput(project);
ApplyRepositorySuggestions(project);
}

public void UpdateGitHubRepositoryInput(ProjectModalFormModel project, string? repositoryInput)
{
project.GitHubRepositoryInput = repositoryInput?.Trim() ?? string.Empty;
SyncOwnerFromRepositoryInput(project);
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
=> project.CreationMode == mode ? "btn-solid theme-primary" : "btn-solid theme-secondary";

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

SuggestName(project, repositoryName);
}

/// <summary>
/// An <c>owner/repo</c> typed into a new repository's name takes precedence, so the owner
/// picker follows it (matching a loaded account's casing) instead of contradicting it.
/// </summary>
private void SyncOwnerFromRepositoryInput(ProjectModalFormModel project)
{
if (project.CreationMode != ProjectCreationMode.CreateGitHubRepository ||
_gitHubOwners.Count == 0 ||
!TryGetTypedOwner(project.GitHubRepositoryInput, out var typedOwner))
{
return;
}

project.NewGitHubRepositoryOwner = _gitHubOwners.FirstOrDefault(owner =>
string.Equals(owner, typedOwner, StringComparison.OrdinalIgnoreCase)) ?? typedOwner;
}

private static bool TryGetTypedOwner(string? value, out string owner)
{
owner = string.Empty;
if (string.IsNullOrWhiteSpace(value))
{
return false;
}

var segments = value.Trim().Trim('/').Replace('\\', '/')
.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (segments.Length < 2)
{
return false;
}

owner = segments[0];
return true;
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
