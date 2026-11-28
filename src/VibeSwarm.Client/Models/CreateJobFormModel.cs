using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Client.Models;

public sealed class CreateJobFormModel : IValidatableObject
{
	[StringLength(ValidationLimits.JobGoalPromptMaxLength, ErrorMessage = "Goal prompt must be {1:N0} characters or fewer.")]
	public string GoalPrompt { get; set; } = string.Empty;

	/// <summary>
	/// The profile the job runs on. Left empty, the job runs on the project's Build profile.
	/// </summary>
	public Guid? ProfileId { get; set; }


	/// <summary>
	/// On a project that works as a team: whether this job is worked by the team (planned by the
	/// Lead, built in steps, reviewed) or by its builder alone. Ignored elsewhere.
	/// </summary>
	public bool WorkAsTeam { get; set; } = true;

	[StringLength(250)]
	public string? Branch { get; set; }

	public GitChangeDeliveryMode GitChangeDeliveryMode { get; set; } = GitChangeDeliveryMode.CommitToBranch;

	[StringLength(250)]
	public string? TargetBranch { get; set; }

	/// <summary>How many copies of the job run side by side (see <see cref="Job.RequestedInstances"/>).</summary>
	[Range(1, ValidationLimits.JobInstancesMax, ErrorMessage = "Instances must be between {1} and {2}.")]
	public int Instances { get; set; } = 1;

	public static CreateJobFormModel FromJob(Job job)
	{
		return new CreateJobFormModel
		{
			GoalPrompt = job.GoalPrompt ?? string.Empty,
			ProfileId = job.ProfileId,
			WorkAsTeam = !job.RunSolo,
			Branch = job.Branch,
			GitChangeDeliveryMode = job.GitChangeDeliveryMode,
			TargetBranch = job.TargetBranch,
			Instances = Math.Clamp(job.RequestedInstances, 1, ValidationLimits.JobInstancesMax)
		};
	}

	public void ApplyTo(Job job)
	{
		job.GoalPrompt = GoalPrompt.Trim();
		// The server works out the provider, model and reasoning from the profile.
		job.ProfileId = ProfileId;
		job.RunSolo = !WorkAsTeam;
		job.ProviderId = Guid.Empty;
		job.ModelUsed = null;
		job.ReasoningEffort = null;
		job.Branch = string.IsNullOrWhiteSpace(Branch) ? null : Branch.Trim();
		job.GitChangeDeliveryMode = GitChangeDeliveryMode;
		job.TargetBranch = string.IsNullOrWhiteSpace(TargetBranch) ? null : TargetBranch.Trim();
		job.RequestedInstances = Math.Clamp(Instances, 1, ValidationLimits.JobInstancesMax);
	}

	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (string.IsNullOrWhiteSpace(GoalPrompt))
		{
			yield return new ValidationResult("Goal prompt is required.", [nameof(GoalPrompt)]);
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
