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

	[StringLength(ValidationLimits.ProjectBranchMaxLength)]
	public string? DevelopmentBranch { get; set; }

	[StringLength(ValidationLimits.ProjectBranchMaxLength)]
	public string? ReleaseBranch { get; set; }

	/// <summary>Whether jobs here are worked by the team: a Lead plans, the builder builds, a reviewer checks.</summary>
	public bool TeamEnabled { get; set; }

	/// <summary>The team roles this project fills with its own profile instead of the team's.</summary>
	public TeamRoles TeamOverrides { get; set; }

	public bool ReviewEnabled { get; set; }

	public Guid? ReviewProviderId { get; set; }

	public Guid? ReviewInferenceProviderId { get; set; }

	[StringLength(ValidationLimits.ProjectPlanningModelIdMaxLength)]
	public string? ReviewModelId { get; set; }

	[StringLength(ValidationLimits.ReasoningEffortMaxLength)]
	public string? ReviewReasoningEffort { get; set; }

	public Guid? ReviewProfileId { get; set; }

	public Guid? LeadProviderId { get; set; }

	[StringLength(ValidationLimits.ProjectPlanningModelIdMaxLength)]
	public string? LeadModelId { get; set; }

	[StringLength(ValidationLimits.ReasoningEffortMaxLength)]
	public string? LeadReasoningEffort { get; set; }

	public Guid? LeadProfileId { get; set; }

	public Guid? DecisionsProfileId { get; set; }

	public Guid? ResearchProfileId { get; set; }

	[StringLength(LeadReviewLimits.DirectionMaxLength)]
	public string? LeadDirection { get; set; }

	public Guid? IdeaInferenceProviderId { get; set; }

	/// <summary>The inference profile that suggests ideas; the idea provider and model follow it.</summary>
	public Guid? IdeasProfileId { get; set; }

	/// <summary>The inference profile that writes commit messages; that provider and model follow it.</summary>
	public Guid? CommitSummaryProfileId { get; set; }

	[StringLength(200)]
	public string? IdeaInferenceModelId { get; set; }

	public Guid? CommitSummaryInferenceProviderId { get; set; }

	[StringLength(200)]
	public string? CommitSummaryInferenceModelId { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }

	public ICollection<ProjectProvider> ProviderSelections { get; set; } = new List<ProjectProvider>();

	public ICollection<ProjectEnvironment> Environments { get; set; } = new List<ProjectEnvironment>();

	public Guid? FolderId { get; set; }

	/// <summary>
	/// Projects checked under Linked projects. Saved separately from the project, because a
	/// link also changes the project at its other end.
	/// </summary>
	public HashSet<Guid> LinkedProjectIds { get; set; } = [];

	[StringLength(ValidationLimits.ProjectPromptContextMaxLength)]
	public string? PromptContext { get; set; }

	[StringLength(ValidationLimits.ProjectMemoryMaxLength)]
	public string? Memory { get; set; }

	public bool IsActive { get; set; } = true;

	public bool BuildVerificationEnabled { get; set; }

	[StringLength(500)]
	public string? BuildCommand { get; set; }

	[StringLength(500)]
	public string? TestCommand { get; set; }

	public ProjectCreationMode CreationMode { get; set; } = ProjectCreationMode.ExistingDirectory;

	[StringLength(ValidationLimits.ProjectGitHubRepositoryMaxLength)]
	public string GitHubRepositoryInput { get; set; } = string.Empty;

	/// <summary>
	/// Account or organization a new repository is created under. Applies only when
	/// <see cref="GitHubRepositoryInput"/> is a bare name; empty leaves it to the GitHub CLI's signed-in user.
	/// </summary>
	[StringLength(ValidationLimits.ProjectGitHubRepositoryMaxLength)]
	public string NewGitHubRepositoryOwner { get; set; } = string.Empty;

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
			DevelopmentBranch = source.DevelopmentBranch,
			ReleaseBranch = source.ReleaseBranch,
			TeamEnabled = source.TeamEnabled,
			TeamOverrides = source.TeamOverrides,
			ReviewEnabled = source.ReviewEnabled,
			ReviewProviderId = source.ReviewProviderId,
			ReviewInferenceProviderId = source.ReviewInferenceProviderId,
			ReviewModelId = source.ReviewModelId,
			ReviewReasoningEffort = source.ReviewReasoningEffort,
			ReviewProfileId = source.ReviewProfileId,
			LeadProviderId = source.LeadProviderId,
			LeadModelId = source.LeadModelId,
			LeadReasoningEffort = source.LeadReasoningEffort,
			LeadProfileId = source.LeadProfileId,
			DecisionsProfileId = source.DecisionsProfileId,
			ResearchProfileId = source.ResearchProfileId,
			LeadDirection = source.LeadDirection,
			IdeasProfileId = source.IdeasProfileId,
			CommitSummaryProfileId = source.CommitSummaryProfileId,
			IdeaInferenceProviderId = source.IdeaInferenceProviderId,
			IdeaInferenceModelId = source.IdeaInferenceModelId,
			CommitSummaryInferenceProviderId = source.CommitSummaryInferenceProviderId,
			CommitSummaryInferenceModelId = source.CommitSummaryInferenceModelId,
			CreatedAt = source.CreatedAt,
			UpdatedAt = source.UpdatedAt,
			ProviderSelections = source.ProviderSelections.ToList(),
			Environments = source.Environments.ToList(),
			FolderId = source.FolderId,
			LinkedProjectIds = (source.Links ?? []).Select(link => link.LinkedProjectId).ToHashSet(),
			PromptContext = source.PromptContext,
			Memory = source.Memory,
			IsActive = source.IsActive,
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
			DevelopmentBranch = string.IsNullOrWhiteSpace(DevelopmentBranch) ? null : DevelopmentBranch.Trim(),
			ReleaseBranch = string.IsNullOrWhiteSpace(ReleaseBranch) ? null : ReleaseBranch.Trim(),
			TeamEnabled = TeamEnabled,
			TeamOverrides = TeamOverrides,
			ReviewEnabled = ReviewEnabled,
			ReviewProviderId = ReviewProviderId,
			ReviewInferenceProviderId = ReviewProviderId.HasValue ? null : ReviewInferenceProviderId,
			ReviewModelId = string.IsNullOrWhiteSpace(ReviewModelId) ? null : ReviewModelId.Trim(),
			ReviewReasoningEffort = ReviewProviderId.HasValue ? ProviderCapabilities.NormalizeReasoningEffort(ReviewReasoningEffort) : null,
			ReviewProfileId = ReviewProfileId,
			LeadProviderId = LeadProviderId,
			LeadModelId = string.IsNullOrWhiteSpace(LeadModelId) ? null : LeadModelId.Trim(),
			LeadReasoningEffort = LeadProviderId.HasValue ? ProviderCapabilities.NormalizeReasoningEffort(LeadReasoningEffort) : null,
			LeadProfileId = LeadProfileId,
			DecisionsProfileId = DecisionsProfileId,
			ResearchProfileId = ResearchProfileId,
			LeadDirection = string.IsNullOrWhiteSpace(LeadDirection) ? null : LeadDirection.Trim(),
			IdeasProfileId = IdeasProfileId,
			CommitSummaryProfileId = CommitSummaryProfileId,
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
				ProfileId = s.ProfileId,
				CreatedAt = s.CreatedAt,
				UpdatedAt = s.UpdatedAt
			}).ToList(),
			Environments = Environments.ToList(),
			FolderId = FolderId,
			PromptContext = string.IsNullOrWhiteSpace(PromptContext) ? null : PromptContext.Trim(),
			Memory = string.IsNullOrWhiteSpace(Memory) ? null : Memory.Trim(),
			BuildVerificationEnabled = BuildVerificationEnabled,
			BuildCommand = string.IsNullOrWhiteSpace(BuildCommand) ? null : BuildCommand.Trim(),
			TestCommand = string.IsNullOrWhiteSpace(TestCommand) ? null : TestCommand.Trim(),
			IsActive = IsActive
		};
	}

	public string? GetNormalizedGitHubRepository()
	{
		var isCreate = CreationMode == ProjectCreationMode.CreateGitHubRepository;
		if (!TryNormalizeGitHubRepository(GitHubRepositoryInput, isCreate, out var normalizedRepository))
		{
			return null;
		}

		// An owner typed into the repository field wins over the picked one.
		return isCreate && !normalizedRepository.Contains('/') && !string.IsNullOrWhiteSpace(NewGitHubRepositoryOwner)
			? $"{NewGitHubRepositoryOwner.Trim()}/{normalizedRepository}"
			: normalizedRepository;
	}

	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (BuildVerificationEnabled && string.IsNullOrWhiteSpace(BuildCommand))
		{
			yield return new ValidationResult(
				"Build command is required when build verification is enabled.",
				[nameof(BuildCommand)]);
		}

		if (!string.IsNullOrWhiteSpace(DevelopmentBranch) &&
			string.Equals(DevelopmentBranch.Trim(), ReleaseBranch?.Trim(), StringComparison.Ordinal))
		{
			yield return new ValidationResult(
				"The development and release branches must be different.",
				[nameof(ReleaseBranch)]);
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
