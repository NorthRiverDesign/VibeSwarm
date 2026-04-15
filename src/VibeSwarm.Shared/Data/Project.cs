using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Specifies the auto-commit behavior after a job completes successfully.
/// </summary>
public enum AutoCommitMode
{
    /// <summary>
    /// No automatic commit. Changes must be committed manually.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Automatically commit changes after job completion, but do not push.
    /// </summary>
    CommitOnly = 1,

    /// <summary>
    /// Automatically commit and push changes after job completion.
    /// </summary>
    CommitAndPush = 2
}

public class Project
{
	public Guid Id { get; set; }

    [Required]
    [StringLength(ValidationLimits.ProjectNameMaxLength, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(ValidationLimits.ProjectDescriptionMaxLength)]
    public string? Description { get; set; }

    [Required]
    [StringLength(ValidationLimits.ProjectWorkingPathMaxLength, MinimumLength = 1)]
    public string WorkingPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional GitHub repository in "owner/repo" format.
    /// Used when creating a project from a GitHub repository.
    /// </summary>
    [StringLength(ValidationLimits.ProjectGitHubRepositoryMaxLength)]
    public string? GitHubRepository { get; set; }

	/// <summary>
	/// Auto-commit behavior after job completion.
	/// </summary>
	public AutoCommitMode AutoCommitMode { get; set; } = AutoCommitMode.Off;

	/// <summary>
	/// Controls whether job results stay on a branch or should be delivered through a pull request.
	/// </summary>
	public GitChangeDeliveryMode GitChangeDeliveryMode { get; set; } = GitChangeDeliveryMode.CommitToBranch;

	/// <summary>
	/// Optional default target branch used when jobs create pull requests or merge changes.
	/// </summary>
	[StringLength(ValidationLimits.ProjectDefaultTargetBranchMaxLength)]
	public string? DefaultTargetBranch { get; set; }

	/// <summary>
	/// Whether idea expansion should use provider-native planning mode.
	/// When enabled, idea expansion uses the configured planning provider/model to generate
	/// a reviewed implementation plan before the idea is converted into a job.
	/// </summary>
	public bool PlanningEnabled { get; set; }

	/// <summary>
	/// Optional provider to use for project planning.
	/// Supported planning providers are currently Claude and GitHub Copilot.
	/// </summary>
	public Guid? PlanningProviderId { get; set; }

	/// <summary>
	/// Optional model override to use for project planning.
	/// When omitted, the planning provider's default model is used.
	/// </summary>
	[StringLength(ValidationLimits.ProjectPlanningModelIdMaxLength)]
	public string? PlanningModelId { get; set; }

	/// <summary>
	/// Optional reasoning effort override to use for project planning.
	/// When omitted, the planning provider's default reasoning is used.
	/// </summary>
	[StringLength(ValidationLimits.ReasoningEffortMaxLength)]
	public string? PlanningReasoningEffort { get; set; }

	/// <summary>
	/// Default inference provider for idea generation (e.g., Grok, Ollama).
	/// Used as the default in Auto-Pilot and suggestion workflows.
	/// </summary>
	public Guid? IdeaInferenceProviderId { get; set; }

	/// <summary>
	/// Default inference model for idea generation. When null, uses the provider's default.
	/// </summary>
	[StringLength(200)]
	public string? IdeaInferenceModelId { get; set; }

	/// <summary>
	/// Optional inference provider used to generate auto-commit summaries.
	/// When null, commit summaries continue to come from the coding provider output.
	/// </summary>
	public Guid? CommitSummaryInferenceProviderId { get; set; }

	/// <summary>
	/// Optional inference model used to generate auto-commit summaries.
	/// When null, the selected inference provider's default commit-summary/default model is used.
	/// </summary>
	[StringLength(200)]
	public string? CommitSummaryInferenceModelId { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public ICollection<Job> Jobs { get; set; } = new List<Job>();

    public ICollection<Idea> Ideas { get; set; } = new List<Idea>();

    public ICollection<ProjectProvider> ProviderSelections { get; set; } = new List<ProjectProvider>();

    public ICollection<ProjectAgent> AgentAssignments { get; set; } = new List<ProjectAgent>();

    public ICollection<ProjectEnvironment> Environments { get; set; } = new List<ProjectEnvironment>();

    /// <summary>
    /// Optional per-project instructions injected into every job prompt.
    /// E.g., coding conventions, framework preferences, language requirements.
    /// </summary>
    [StringLength(ValidationLimits.ProjectPromptContextMaxLength)]
    public string? PromptContext { get; set; }

    /// <summary>
    /// Persistent per-project memory captured across agent runs.
    /// Agents should read this before starting work and update it when they discover durable
    /// project context or correct a mistake so future runs do not repeat it.
    /// </summary>
    [StringLength(ValidationLimits.ProjectMemoryMaxLength)]
    public string? Memory { get; set; }

    /// <summary>
    /// Cached repo map (compact file tree) generated from the project directory.
    /// Injected into the system prompt to give agents a head start on project structure.
    /// </summary>
    public string? RepoMap { get; set; }

    /// <summary>
    /// When the repo map was last generated. Used to determine staleness (regenerate after 24h).
    /// </summary>
    public DateTime? RepoMapGeneratedAt { get; set; }

    /// <summary>
    /// Whether the project is active. Inactive projects are hidden from the dashboard
    /// and shown with reduced emphasis in other views.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether Ideas auto-processing is currently running for this project
    /// </summary>
    public bool IdeasProcessingActive { get; set; }

	/// <summary>
	/// Whether to auto-commit changes when jobs from ideas complete during auto-processing
	/// </summary>
	public bool IdeasAutoCommit { get; set; }

	/// <summary>
	/// Optional provider override used while Start All idea auto-processing is active.
	/// </summary>
	public Guid? IdeasProcessingProviderId { get; set; }

	/// <summary>
	/// Optional model override used while Start All idea auto-processing is active.
	/// </summary>
	[StringLength(ValidationLimits.JobScheduleModelIdMaxLength)]
	public string? IdeasProcessingModelId { get; set; }

	/// <summary>
	/// When enabled and at least two team roles are configured, creating a job automatically
	/// fans out into parallel role-based jobs — one per enabled team role assignment.
	/// Each role job runs its assigned provider with a role-specific system prompt.
	/// </summary>
	public bool EnableTeamSwarm { get; set; }

	/// <summary>
	/// Whether to verify the build succeeds before auto-committing or pushing job results.
	/// When enabled, the configured BuildCommand is executed after job completion.
	/// If the build fails, changes are not auto-committed or pushed.
	/// </summary>
	public bool BuildVerificationEnabled { get; set; }

	/// <summary>
	/// Shell command to verify the project builds successfully (e.g., "dotnet build", "npm run build", "cargo build").
	/// Executed in the project's working directory after a job completes and before auto-commit.
	/// </summary>
	[StringLength(500)]
	public string? BuildCommand { get; set; }

	/// <summary>
	/// Optional shell command to verify tests pass (e.g., "dotnet test", "npm test", "cargo test").
	/// Executed after the build command succeeds. If tests fail, changes are not auto-committed.
	/// </summary>
	[StringLength(500)]
	public string? TestCommand { get; set; }
}
