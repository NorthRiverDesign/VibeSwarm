using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Client.Models;

public sealed class CreateJobFormModel : IValidatableObject
{
	[StringLength(ValidationLimits.JobTemplatePromptMaxLength, ErrorMessage = "Goal prompt must be 2000 characters or fewer.")]
	public string GoalPrompt { get; set; } = string.Empty;

	[Required(ErrorMessage = "Please select a provider.")]
	public Guid? ProviderId { get; set; }

	public Guid? AgentId { get; set; }

	public bool AllowBlankGoalPrompt { get; set; }

	[StringLength(VibeSwarm.Shared.Validation.ValidationLimits.ReasoningEffortMaxLength)]
	public string? ReasoningEffort { get; set; }

	[StringLength(250)]
	public string? Branch { get; set; }

	public GitChangeDeliveryMode GitChangeDeliveryMode { get; set; } = GitChangeDeliveryMode.CommitToBranch;

	[StringLength(250)]
	public string? TargetBranch { get; set; }

	public CycleMode CycleMode { get; set; } = CycleMode.SingleCycle;

	public CycleSessionMode CycleSessionMode { get; set; } = CycleSessionMode.ContinueSession;

	[Range(1, 100, ErrorMessage = "Max cycles must be between 1 and 100.")]
	public int MaxCycles { get; set; } = 1;

	[StringLength(2000)]
	public string? CycleReviewPrompt { get; set; }

	public static CreateJobFormModel FromJob(Job job)
	{
		return new CreateJobFormModel
		{
			GoalPrompt = job.GoalPrompt ?? string.Empty,
			ProviderId = job.ProviderId == Guid.Empty ? null : job.ProviderId,
			AgentId = job.AgentId,
			ReasoningEffort = job.ReasoningEffort,
			Branch = job.Branch,
			GitChangeDeliveryMode = job.GitChangeDeliveryMode,
			TargetBranch = job.TargetBranch,
			CycleMode = job.CycleMode,
			CycleSessionMode = job.CycleSessionMode,
			MaxCycles = job.MaxCycles,
			CycleReviewPrompt = job.CycleReviewPrompt
		};
	}

	public void ApplyTo(Job job)
	{
		job.GoalPrompt = GoalPrompt.Trim();
		job.ProviderId = ProviderId ?? Guid.Empty;
		job.AgentId = AgentId == Guid.Empty ? null : AgentId;
		job.ReasoningEffort = VibeSwarm.Shared.Providers.ProviderCapabilities.NormalizeReasoningEffort(ReasoningEffort);
		job.Branch = string.IsNullOrWhiteSpace(Branch) ? null : Branch.Trim();
		job.GitChangeDeliveryMode = GitChangeDeliveryMode;
		job.TargetBranch = string.IsNullOrWhiteSpace(TargetBranch) ? null : TargetBranch.Trim();
		job.CycleMode = CycleMode;
		job.CycleSessionMode = CycleSessionMode;
		job.MaxCycles = MaxCycles;
		job.CycleReviewPrompt = string.IsNullOrWhiteSpace(CycleReviewPrompt) ? null : CycleReviewPrompt.Trim();
	}

	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (string.IsNullOrWhiteSpace(GoalPrompt) && !AllowBlankGoalPrompt)
		{
			yield return new ValidationResult(
				AgentId.HasValue && AgentId != Guid.Empty
					? "Goal prompt is required because the selected agent does not have reusable instructions."
					: "Goal prompt is required.",
				[nameof(GoalPrompt)]);
		}

		if (GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest &&
			string.IsNullOrWhiteSpace(TargetBranch))
		{
			yield return new ValidationResult(
				"Target branch is required when creating a pull request.",
				[nameof(TargetBranch)]);
		}
	}
}
