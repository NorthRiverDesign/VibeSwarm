using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Client.Models;

public sealed class CreateJobFormModel : IValidatableObject
{
	[Required(ErrorMessage = "Goal prompt is required.")]
	[StringLength(2000, MinimumLength = 1, ErrorMessage = "Goal prompt must be between 1 and 2000 characters.")]
	public string GoalPrompt { get; set; } = string.Empty;

	[Required(ErrorMessage = "Please select a provider.")]
	public Guid? ProviderId { get; set; }

	public Guid? AgentId { get; set; }

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
			GoalPrompt = job.GoalPrompt,
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
		if (GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest &&
			string.IsNullOrWhiteSpace(TargetBranch))
		{
			yield return new ValidationResult(
				"Target branch is required when creating a pull request.",
				[nameof(TargetBranch)]);
		}
	}
}
