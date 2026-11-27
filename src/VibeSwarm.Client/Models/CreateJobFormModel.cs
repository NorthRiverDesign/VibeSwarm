using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Client.Models;

public sealed class CreateJobFormModel : IValidatableObject
{
	[StringLength(ValidationLimits.JobGoalPromptMaxLength, ErrorMessage = "Goal prompt must be {1:N0} characters or fewer.")]
	public string GoalPrompt { get; set; } = string.Empty;

	/// <summary>
	/// The profile the job runs on. Left empty, the job runs on the project's Build profile; an
	/// agent's job runs on the agent's own.
	/// </summary>
	public Guid? ProfileId { get; set; }

	public Guid? AgentId { get; set; }

	public bool AllowBlankGoalPrompt { get; set; }

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

	/// <summary>How many copies of the job run side by side (see <see cref="Job.RequestedInstances"/>).</summary>
	[Range(1, ValidationLimits.JobInstancesMax, ErrorMessage = "Instances must be between {1} and {2}.")]
	public int Instances { get; set; } = 1;

	public static CreateJobFormModel FromJob(Job job)
	{
		return new CreateJobFormModel
		{
			GoalPrompt = job.GoalPrompt ?? string.Empty,
			ProfileId = job.ProfileId,
			AgentId = job.AgentId,
			Branch = job.Branch,
			GitChangeDeliveryMode = job.GitChangeDeliveryMode,
			TargetBranch = job.TargetBranch,
			CycleMode = job.CycleMode,
			CycleSessionMode = job.CycleSessionMode,
			MaxCycles = job.MaxCycles,
			CycleReviewPrompt = job.CycleReviewPrompt,
			Instances = Math.Clamp(job.RequestedInstances, 1, ValidationLimits.JobInstancesMax)
		};
	}

	public void ApplyTo(Job job)
	{
		job.GoalPrompt = GoalPrompt.Trim();
		job.AgentId = AgentId == Guid.Empty ? null : AgentId;
		// The server works out the provider, model and reasoning: from the profile, or from the
		// agent when the job runs as one.
		job.ProfileId = job.AgentId.HasValue ? null : ProfileId;
		job.ProviderId = Guid.Empty;
		job.ModelUsed = null;
		job.ReasoningEffort = null;
		job.Branch = string.IsNullOrWhiteSpace(Branch) ? null : Branch.Trim();
		job.GitChangeDeliveryMode = GitChangeDeliveryMode;
		job.TargetBranch = string.IsNullOrWhiteSpace(TargetBranch) ? null : TargetBranch.Trim();
		job.CycleMode = CycleMode;
		job.CycleSessionMode = CycleSessionMode;
		job.MaxCycles = MaxCycles;
		job.CycleReviewPrompt = string.IsNullOrWhiteSpace(CycleReviewPrompt) ? null : CycleReviewPrompt.Trim();
		// An agent's job runs as that agent alone.
		job.RequestedInstances = job.AgentId.HasValue ? 1 : Math.Clamp(Instances, 1, ValidationLimits.JobInstancesMax);
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
