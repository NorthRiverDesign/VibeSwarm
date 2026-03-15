using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Client.Models;

public sealed class ProjectModalState
{
	private string? _defaultProjectsDirectory;

	public ProjectCreationMode CreationMode { get; private set; } = ProjectCreationMode.ExistingDirectory;

	public string GitHubRepositoryInput { get; private set; } = string.Empty;

	public string? GitHubRepositoryError { get; private set; }

	public string? GitHubRepositoryDescriptionError { get; private set; }

	public string NewGitHubRepositoryDescription { get; set; } = string.Empty;

	public bool NewGitHubRepositoryPrivate { get; set; }

	public string NewGitHubGitignoreTemplate { get; set; } = string.Empty;

	public string NewGitHubLicenseTemplate { get; set; } = string.Empty;

	public bool NewGitHubInitializeReadme { get; set; }

	public string WorkingPathPlaceholder => !string.IsNullOrEmpty(_defaultProjectsDirectory)
		? Path.Combine(_defaultProjectsDirectory, "project-name")
		: (OperatingSystem.IsWindows() ? @"C:\projects\myapp" : "/home/user/projects/myapp");

	public void SetDefaultProjectsDirectory(string? defaultProjectsDirectory)
	{
		_defaultProjectsDirectory = defaultProjectsDirectory;
	}

	public void Reset()
	{
		CreationMode = ProjectCreationMode.ExistingDirectory;
		GitHubRepositoryInput = string.Empty;
		GitHubRepositoryError = null;
		GitHubRepositoryDescriptionError = null;
		NewGitHubRepositoryDescription = string.Empty;
		NewGitHubRepositoryPrivate = false;
		NewGitHubGitignoreTemplate = string.Empty;
		NewGitHubLicenseTemplate = string.Empty;
		NewGitHubInitializeReadme = false;
	}

	public void LoadFromProject(Project project)
	{
		GitHubRepositoryInput = project.GitHubRepository ?? string.Empty;
		GitHubRepositoryError = null;
		GitHubRepositoryDescriptionError = null;
	}

	public void SelectSourceMode(Project project, ProjectCreationMode mode)
	{
		CreationMode = mode;
		GitHubRepositoryError = null;
		GitHubRepositoryDescriptionError = null;

		if (CreationMode == ProjectCreationMode.CreateGitHubRepository &&
			string.IsNullOrWhiteSpace(GitHubRepositoryInput) &&
			!string.IsNullOrWhiteSpace(project.Name))
		{
			GitHubRepositoryInput = SanitizeRepositoryName(project.Name);
		}

		ApplyRepositorySuggestions(project);
	}

	public void UpdateGitHubRepositoryInput(Project project, string? repositoryInput)
	{
		GitHubRepositoryInput = repositoryInput?.Trim() ?? string.Empty;
		GitHubRepositoryError = null;
		GitHubRepositoryDescriptionError = null;
		ApplyRepositorySuggestions(project);
	}

	public bool Validate(Project project)
	{
		GitHubRepositoryError = null;
		GitHubRepositoryDescriptionError = null;

		if (CreationMode == ProjectCreationMode.ExistingDirectory)
		{
			return true;
		}

		var allowOwnerOnly = CreationMode == ProjectCreationMode.CreateGitHubRepository;
		if (TryNormalizeGitHubRepository(GitHubRepositoryInput, allowOwnerOnly, out _))
		{
			if (!string.IsNullOrWhiteSpace(NewGitHubRepositoryDescription) &&
				NewGitHubRepositoryDescription.Trim().Length > ValidationLimits.ProjectGitHubDescriptionMaxLength)
			{
				GitHubRepositoryDescriptionError = $"Repository description must be {ValidationLimits.ProjectGitHubDescriptionMaxLength:N0} characters or fewer.";
				return false;
			}

			return true;
		}

		GitHubRepositoryError = allowOwnerOnly
			? "GitHub repositories must use the format 'repo' or 'owner/repo'."
			: "GitHub repositories must use the format 'owner/repo'.";

		return false;
	}

	public bool TryMapSubmissionError(string? errorMessage)
	{
		if (string.IsNullOrWhiteSpace(errorMessage) ||
			(!errorMessage.Contains("GitHub repository", StringComparison.OrdinalIgnoreCase) &&
			 !errorMessage.Contains("GitHub repositories", StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}

		GitHubRepositoryError = errorMessage;
		return true;
	}

	public ProjectCreationRequest BuildProjectCreationRequest(Project project)
	{
		var request = new ProjectCreationRequest
		{
			Project = project,
			Mode = CreationMode
		};

		if (CreationMode == ProjectCreationMode.CloneGitHubRepository)
		{
			request.GitHub = new GitHubRepositoryOptions
			{
				Repository = GitHubRepositoryInput
			};
		}
		else if (CreationMode == ProjectCreationMode.CreateGitHubRepository)
		{
			request.GitHub = new GitHubRepositoryOptions
			{
				Repository = GitHubRepositoryInput,
				Description = string.IsNullOrWhiteSpace(NewGitHubRepositoryDescription) ? null : NewGitHubRepositoryDescription.Trim(),
				IsPrivate = NewGitHubRepositoryPrivate,
				GitignoreTemplate = string.IsNullOrWhiteSpace(NewGitHubGitignoreTemplate) ? null : NewGitHubGitignoreTemplate,
				LicenseTemplate = string.IsNullOrWhiteSpace(NewGitHubLicenseTemplate) ? null : NewGitHubLicenseTemplate,
				InitializeReadme = NewGitHubInitializeReadme
			};
		}

		return request;
	}

	public string GetSourceModeButtonClass(ProjectCreationMode mode) => CreationMode == mode ? "btn-primary" : "btn-secondary";

	public string GetWorkingPathHelpText() => CreationMode switch
	{
		ProjectCreationMode.CloneGitHubRepository => "Repository contents will be cloned into this directory when the project is created.",
		ProjectCreationMode.CreateGitHubRepository => "This directory will become the local workspace for the new GitHub repository.",
		_ => "Directory where CLI providers execute commands."
	};

	public string GetProgressMessage() => CreationMode switch
	{
		ProjectCreationMode.CloneGitHubRepository => "Cloning GitHub repository...",
		ProjectCreationMode.CreateGitHubRepository => "Creating GitHub repository...",
		_ => "Creating project..."
	};

	public string GetSuccessMessage() => CreationMode switch
	{
		ProjectCreationMode.CloneGitHubRepository => "Project created and GitHub repository cloned successfully.",
		ProjectCreationMode.CreateGitHubRepository => "Project created and GitHub repository linked successfully.",
		_ => "Project created successfully."
	};

	public string GetSubmitButtonText(bool isSaving, bool isEdit)
	{
		if (isSaving)
		{
			return "Saving...";
		}

		if (isEdit)
		{
			return "Update Project";
		}

		return CreationMode switch
		{
			ProjectCreationMode.CloneGitHubRepository => "Clone & Create",
			ProjectCreationMode.CreateGitHubRepository => "Create Repo & Project",
			_ => "Create Project"
		};
	}

	private void ApplyRepositorySuggestions(Project project)
	{
		var repositoryName = ExtractRepositoryName(GitHubRepositoryInput);
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

	private static bool TryNormalizeGitHubRepository(string? value, bool allowOwnerOnly, out string normalizedRepository)
	{
		normalizedRepository = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		var trimmed = value.Trim().Trim('/');
		if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			trimmed = trimmed[..^4];
		}

		trimmed = trimmed.Replace('\\', '/');
		var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length == 2 &&
			!string.IsNullOrWhiteSpace(parts[0]) &&
			!string.IsNullOrWhiteSpace(parts[1]))
		{
			normalizedRepository = $"{parts[0]}/{parts[1]}";
			return true;
		}

		if (allowOwnerOnly && parts.Length == 1 && !string.IsNullOrWhiteSpace(parts[0]))
		{
			normalizedRepository = parts[0];
			return true;
		}

		return false;
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
