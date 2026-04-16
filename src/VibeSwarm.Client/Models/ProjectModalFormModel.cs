using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Client.Models;

public sealed class ProjectModalFormModel : IValidatableObject
{
	public Guid Id { get; set; }

	[Required(ErrorMessage = "Project name is required.")]
	[StringLength(ValidationLimits.ProjectNameMaxLength, MinimumLength = 1, ErrorMessage = "Project name is required.")]
	public string Name { get; set; } = string.Empty;

	[StringLength(ValidationLimits.ProjectDescriptionMaxLength)]
	public string? Description { get; set; }

	[Required(ErrorMessage = "Working path is required.")]
	[StringLength(ValidationLimits.ProjectWorkingPathMaxLength, MinimumLength = 1, ErrorMessage = "Working path is required.")]
	public string WorkingPath { get; set; } = string.Empty;

	[StringLength(ValidationLimits.ProjectGitHubRepositoryMaxLength)]
	public string? GitHubRepository { get; set; }

	public AutoCommitMode AutoCommitMode { get; set; } = AutoCommitMode.Off;

	public GitChangeDeliveryMode GitChangeDeliveryMode { get; set; } = GitChangeDeliveryMode.CommitToBranch;

	[StringLength(ValidationLimits.ProjectDefaultTargetBranchMaxLength)]
	public string? DefaultTargetBranch { get; set; }

	public bool PlanningEnabled { get; set; }

	public Guid? PlanningProviderId { get; set; }

	[StringLength(ValidationLimits.ProjectPlanningModelIdMaxLength)]
	public string? PlanningModelId { get; set; }

	[StringLength(ValidationLimits.ReasoningEffortMaxLength)]
	public string? PlanningReasoningEffort { get; set; }

	public Guid? IdeaInferenceProviderId { get; set; }

	[StringLength(200)]
	public string? IdeaInferenceModelId { get; set; }

	public Guid? CommitSummaryInferenceProviderId { get; set; }

	[StringLength(200)]
	public string? CommitSummaryInferenceModelId { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }

	public ICollection<ProjectProvider> ProviderSelections { get; set; } = new List<ProjectProvider>();

	public ICollection<ProjectAgent> AgentAssignments { get; set; } = new List<ProjectAgent>();

	public ICollection<ProjectEnvironment> Environments { get; set; } = new List<ProjectEnvironment>();

	[StringLength(ValidationLimits.ProjectPromptContextMaxLength)]
	public string? PromptContext { get; set; }

	[StringLength(ValidationLimits.ProjectMemoryMaxLength)]
	public string? Memory { get; set; }

	public bool IsActive { get; set; } = true;

	public bool EnableTeamSwarm { get; set; }

	public bool BuildVerificationEnabled { get; set; }

	[StringLength(500)]
	public string? BuildCommand { get; set; }

	[StringLength(500)]
	public string? TestCommand { get; set; }

	public ProjectCreationMode CreationMode { get; set; } = ProjectCreationMode.ExistingDirectory;

	[StringLength(ValidationLimits.ProjectGitHubRepositoryMaxLength)]
	public string GitHubRepositoryInput { get; set; } = string.Empty;

	[StringLength(ValidationLimits.ProjectGitHubDescriptionMaxLength)]
	public string NewGitHubRepositoryDescription { get; set; } = string.Empty;

	public bool NewGitHubRepositoryPrivate { get; set; }

	public string NewGitHubGitignoreTemplate { get; set; } = string.Empty;

	public string NewGitHubLicenseTemplate { get; set; } = string.Empty;

	public bool NewGitHubInitializeReadme { get; set; }

	public static ProjectModalFormModel FromProject(Project source)
	{
		return new ProjectModalFormModel
		{
			Id = source.Id,
			Name = source.Name,
			Description = source.Description,
			WorkingPath = source.WorkingPath,
			GitHubRepository = source.GitHubRepository,
			AutoCommitMode = source.AutoCommitMode,
			GitChangeDeliveryMode = source.GitChangeDeliveryMode,
			DefaultTargetBranch = source.DefaultTargetBranch,
			PlanningEnabled = source.PlanningEnabled,
			PlanningProviderId = source.PlanningProviderId,
			PlanningModelId = source.PlanningModelId,
			PlanningReasoningEffort = source.PlanningReasoningEffort,
			IdeaInferenceProviderId = source.IdeaInferenceProviderId,
			IdeaInferenceModelId = source.IdeaInferenceModelId,
			CommitSummaryInferenceProviderId = source.CommitSummaryInferenceProviderId,
			CommitSummaryInferenceModelId = source.CommitSummaryInferenceModelId,
			CreatedAt = source.CreatedAt,
			UpdatedAt = source.UpdatedAt,
			ProviderSelections = source.ProviderSelections.ToList(),
			AgentAssignments = source.AgentAssignments.ToList(),
			Environments = source.Environments.ToList(),
			PromptContext = source.PromptContext,
			Memory = source.Memory,
			IsActive = source.IsActive,
			EnableTeamSwarm = source.EnableTeamSwarm,
			BuildVerificationEnabled = source.BuildVerificationEnabled,
			BuildCommand = source.BuildCommand,
			TestCommand = source.TestCommand,
			GitHubRepositoryInput = source.GitHubRepository ?? string.Empty
		};
	}

	public Project ToProject()
	{
		return new Project
		{
			Id = Id,
			Name = Name.Trim(),
			Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
			WorkingPath = WorkingPath.Trim(),
			GitHubRepository = ResolvePersistedGitHubRepository(),
			AutoCommitMode = AutoCommitMode,
			GitChangeDeliveryMode = GitChangeDeliveryMode,
			DefaultTargetBranch = string.IsNullOrWhiteSpace(DefaultTargetBranch) ? null : DefaultTargetBranch.Trim(),
			PlanningEnabled = PlanningEnabled,
			PlanningProviderId = PlanningProviderId,
			PlanningModelId = string.IsNullOrWhiteSpace(PlanningModelId) ? null : PlanningModelId.Trim(),
			PlanningReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(PlanningReasoningEffort),
			IdeaInferenceProviderId = IdeaInferenceProviderId,
			IdeaInferenceModelId = string.IsNullOrWhiteSpace(IdeaInferenceModelId) ? null : IdeaInferenceModelId.Trim(),
			CommitSummaryInferenceProviderId = CommitSummaryInferenceProviderId,
			CommitSummaryInferenceModelId = string.IsNullOrWhiteSpace(CommitSummaryInferenceModelId) ? null : CommitSummaryInferenceModelId.Trim(),
			CreatedAt = CreatedAt,
			UpdatedAt = UpdatedAt,
			ProviderSelections = ProviderSelections.Select(s => new ProjectProvider
			{
				Id = s.Id,
				ProjectId = s.ProjectId,
				ProviderId = s.ProviderId,
				Priority = s.Priority,
				IsEnabled = s.IsEnabled,
				PreferredModelId = s.PreferredModelId,
				PreferredReasoningEffort = s.PreferredReasoningEffort,
				CreatedAt = s.CreatedAt,
				UpdatedAt = s.UpdatedAt
			}).ToList(),
			AgentAssignments = AgentAssignments.Select(a => new ProjectAgent
			{
				Id = a.Id,
				ProjectId = a.ProjectId,
				AgentId = a.AgentId,
				ProviderId = a.ProviderId,
				PreferredModelId = a.PreferredModelId,
				PreferredReasoningEffort = a.PreferredReasoningEffort,
				IsEnabled = a.IsEnabled,
				CreatedAt = a.CreatedAt,
				UpdatedAt = a.UpdatedAt
			}).ToList(),
			Environments = Environments.ToList(),
			PromptContext = string.IsNullOrWhiteSpace(PromptContext) ? null : PromptContext.Trim(),
			Memory = string.IsNullOrWhiteSpace(Memory) ? null : Memory.Trim(),
			EnableTeamSwarm = EnableTeamSwarm,
			BuildVerificationEnabled = BuildVerificationEnabled,
			BuildCommand = string.IsNullOrWhiteSpace(BuildCommand) ? null : BuildCommand.Trim(),
			TestCommand = string.IsNullOrWhiteSpace(TestCommand) ? null : TestCommand.Trim(),
			IsActive = IsActive
		};
	}

	public string? GetNormalizedGitHubRepository()
	{
		return TryNormalizeGitHubRepository(GitHubRepositoryInput, CreationMode == ProjectCreationMode.CreateGitHubRepository, out var normalizedRepository)
			? normalizedRepository
			: null;
	}

	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (BuildVerificationEnabled && string.IsNullOrWhiteSpace(BuildCommand))
		{
			yield return new ValidationResult(
				"Build command is required when build verification is enabled.",
				[nameof(BuildCommand)]);
		}

		var duplicateAgentIds = AgentAssignments
			.GroupBy(assignment => assignment.AgentId)
			.Where(group => group.Key != Guid.Empty && group.Count() > 1)
			.Select(group => group.Key)
			.ToList();
		if (duplicateAgentIds.Any())
		{
			yield return new ValidationResult(
				"Each agent can only be assigned once per project.",
				[nameof(AgentAssignments)]);
		}

		if (AgentAssignments.Any(assignment => assignment.ProviderId == Guid.Empty))
		{
			yield return new ValidationResult(
				"Each assigned agent must have a provider.",
				[nameof(AgentAssignments)]);
		}

		if (CreationMode == ProjectCreationMode.ExistingDirectory)
		{
			yield break;
		}

		var allowOwnerOnly = CreationMode == ProjectCreationMode.CreateGitHubRepository;
		if (!TryNormalizeGitHubRepository(GitHubRepositoryInput, allowOwnerOnly, out _))
		{
			yield return new ValidationResult(
				allowOwnerOnly
					? "GitHub repositories must use the format 'repo' or 'owner/repo'."
					: "GitHub repositories must use the format 'owner/repo'.",
				[nameof(GitHubRepositoryInput)]);
		}
	}

	private string? ResolvePersistedGitHubRepository()
	{
		if (CreationMode == ProjectCreationMode.ExistingDirectory)
		{
			return string.IsNullOrWhiteSpace(GitHubRepository) ? null : GitHubRepository.Trim();
		}

		return GetNormalizedGitHubRepository();
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
}
