using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// A saved task that can be run on any project. Only the name and prompt are required; every
/// other setting is an optional override, and when it is left empty each project's own setting
/// (provider priority, development branch, delivery mode) applies.
/// </summary>
public class JobTemplate
{
	public Guid Id { get; set; }

	[Required]
	[StringLength(ValidationLimits.JobTemplateNameMaxLength, MinimumLength = 1)]
	public string Name { get; set; } = string.Empty;

	[StringLength(ValidationLimits.JobTemplateDescriptionMaxLength)]
	public string? Description { get; set; }

	[Required]
	[StringLength(ValidationLimits.JobGoalPromptMaxLength, MinimumLength = 1)]
	public string GoalPrompt { get; set; } = string.Empty;

	public Guid? ProviderId { get; set; }
	public Provider? Provider { get; set; }

	[StringLength(ValidationLimits.JobTemplateModelIdMaxLength)]
	public string? ModelId { get; set; }

	[StringLength(ValidationLimits.ReasoningEffortMaxLength)]
	public string? ReasoningEffort { get; set; }

	[StringLength(ValidationLimits.JobTemplateBranchMaxLength)]
	public string? Branch { get; set; }

	/// <summary>How the job's changes are delivered. Null uses each project's own setting.</summary>
	public GitChangeDeliveryMode? GitChangeDeliveryMode { get; set; }

	[StringLength(ValidationLimits.JobTemplateBranchMaxLength)]
	public string? TargetBranch { get; set; }

	public CycleMode CycleMode { get; set; } = CycleMode.SingleCycle;

	public CycleSessionMode CycleSessionMode { get; set; } = CycleSessionMode.ContinueSession;

	[Range(1, 100)]
	public int MaxCycles { get; set; } = 1;

	[StringLength(ValidationLimits.JobTemplatePromptMaxLength)]
	public string? CycleReviewPrompt { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }

	public int UseCount { get; set; }

	/// <summary>When a job was last created from this template. Filled in by the template list; not stored.</summary>
	[NotMapped]
	public DateTime? LastRunAt { get; set; }

	/// <summary>How many projects this template has run on. Filled in by the template list; not stored.</summary>
	[NotMapped]
	public int ProjectRunCount { get; set; }

	public ICollection<Job> Jobs { get; set; } = new List<Job>();
}
