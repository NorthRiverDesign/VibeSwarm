using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

public class JobTemplate
{
	public Guid Id { get; set; }

	[Required]
	[StringLength(ValidationLimits.JobTemplateNameMaxLength, MinimumLength = 1)]
	public string Name { get; set; } = string.Empty;

	[StringLength(ValidationLimits.JobTemplateDescriptionMaxLength)]
	public string? Description { get; set; }

	[Required]
	[StringLength(ValidationLimits.JobTemplatePromptMaxLength, MinimumLength = 1)]
	public string GoalPrompt { get; set; } = string.Empty;

	public Guid? ProviderId { get; set; }
	public Provider? Provider { get; set; }

	[StringLength(ValidationLimits.JobTemplateModelIdMaxLength)]
	public string? ModelId { get; set; }

	[StringLength(ValidationLimits.ReasoningEffortMaxLength)]
	public string? ReasoningEffort { get; set; }

	[StringLength(ValidationLimits.JobTemplateBranchMaxLength)]
	public string? Branch { get; set; }

	public GitChangeDeliveryMode GitChangeDeliveryMode { get; set; } = GitChangeDeliveryMode.CommitToBranch;

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

	public ICollection<Job> Jobs { get; set; } = new List<Job>();
}
