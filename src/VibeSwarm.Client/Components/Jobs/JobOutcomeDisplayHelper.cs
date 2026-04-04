using VibeSwarm.Shared.Data;

namespace VibeSwarm.Client.Components.Jobs;

internal static class JobOutcomeDisplayHelper
{
	public static IReadOnlyList<JobOutcomeBadgeModel> BuildBadges(
		JobStatus status,
		int? changedFilesCount,
		bool? buildVerified,
		string? gitCommitHash,
		string? pullRequestUrl,
		int? pullRequestNumber)
	{
		var badges = new List<JobOutcomeBadgeModel>();
		var hasDeliveredChanges = HasDeliveredChanges(pullRequestUrl, gitCommitHash);
		var hasDetectedChanges = HasDetectedChanges(changedFilesCount, pullRequestUrl, gitCommitHash);

		if (status == JobStatus.Completed && changedFilesCount == 0)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-file-earmark",
				"No changes",
				"The job completed without any detected file changes.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-body-tertiary text-body-secondary"));
		}
		else if (changedFilesCount.GetValueOrDefault() > 0)
		{
			var fileLabel = changedFilesCount == 1 ? "file changed" : "files changed";
			badges.Add(new JobOutcomeBadgeModel(
				"bi-files",
				$"{changedFilesCount} {fileLabel}",
				"The number of files changed by this job.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-body-tertiary text-body-secondary"));
		}

		if (buildVerified == true)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-check2-circle",
				"Checks passed",
				"Configured build and test verification passed after the job finished.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-success-subtle text-success-emphasis"));
		}
		else if (buildVerified == false)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-x-circle",
				"Checks failed",
				"Build or test verification failed after the job finished.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-danger-subtle text-danger-emphasis"));
		}

		if (!string.IsNullOrWhiteSpace(pullRequestUrl))
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-github",
				FormatPullRequestLabel(pullRequestNumber),
				"A pull request was created for this job's changes.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-primary-subtle text-primary-emphasis"));
		}
		else if (!string.IsNullOrWhiteSpace(gitCommitHash))
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-git",
				FormatCommitLabel(gitCommitHash),
				"The job's changes have been committed to git.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-info-subtle text-info-emphasis"));
		}
		else if (hasDetectedChanges && status == JobStatus.Completed)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-send-check",
				"Ready to deliver",
				"The job produced changes, but they have not been committed or delivered yet.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-warning-subtle text-warning-emphasis"));
		}
		else if (hasDetectedChanges && status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Stalled)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-exclamation-triangle",
				"Review changes",
				"The run stopped, but changes are still present for review.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-warning-subtle text-warning-emphasis"));
		}

		if (status == JobStatus.Paused)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-chat-dots",
				"Waiting for input",
				"The job is paused until the user responds.",
				"d-inline-flex align-items-center gap-1 px-2 py-1 rounded-pill small bg-warning-subtle text-warning-emphasis"));
		}

		return badges;
	}

	public static JobOutcomeHintModel? BuildHint(
		JobStatus status,
		int? changedFilesCount,
		bool? buildVerified,
		string? gitCommitHash,
		string? pullRequestUrl,
		int? pullRequestNumber)
	{
		var hasDetectedChanges = HasDetectedChanges(changedFilesCount, pullRequestUrl, gitCommitHash);
		var pullRequestReference = FormatPullRequestReference(pullRequestNumber);

		return status switch
		{
			JobStatus.Completed when buildVerified == false => new JobOutcomeHintModel(
				"bi-shield-x",
				"Checks failed.",
				"Review the verification output before delivering these changes.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-danger-subtle text-danger-emphasis"),
			JobStatus.Completed when !string.IsNullOrWhiteSpace(pullRequestUrl) => new JobOutcomeHintModel(
				"bi-github",
				$"{pullRequestReference} ready.",
				"Review it and merge when the changes are approved.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-success-subtle text-success-emphasis"),
			JobStatus.Completed when !string.IsNullOrWhiteSpace(gitCommitHash) => new JobOutcomeHintModel(
				"bi-git",
				$"{FormatCommitLabel(gitCommitHash)} created.",
				"Push it or open a pull request when you are ready.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-primary-subtle text-primary-emphasis"),
			JobStatus.Completed when changedFilesCount == 0 => new JobOutcomeHintModel(
				"bi-file-earmark",
				"No code changes.",
				"This run finished without any detected file modifications.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-body-tertiary text-body-secondary"),
			JobStatus.Completed when buildVerified == true => new JobOutcomeHintModel(
				"bi-check2-circle",
				"Checks passed.",
				"Review the diff and finish delivery when it looks good.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-success-subtle text-success-emphasis"),
			JobStatus.Completed when hasDetectedChanges => new JobOutcomeHintModel(
				"bi-send-check",
				"Changes ready.",
				"Review the diff and deliver them when you are satisfied.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-warning-subtle text-warning-emphasis"),
			JobStatus.Failed or JobStatus.Cancelled or JobStatus.Stalled when hasDetectedChanges => new JobOutcomeHintModel(
				"bi-exclamation-triangle",
				"Working changes remain.",
				"Review them before retrying, committing, or discarding anything.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-warning-subtle text-warning-emphasis"),
			JobStatus.Failed => new JobOutcomeHintModel(
				"bi-x-circle",
				"Run failed.",
				"Check the transcript before retrying.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-danger-subtle text-danger-emphasis"),
			JobStatus.Cancelled => new JobOutcomeHintModel(
				"bi-slash-circle",
				"Run cancelled.",
				"It stopped before reaching a deliverable result.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-body-tertiary text-body-secondary"),
			JobStatus.Stalled => new JobOutcomeHintModel(
				"bi-exclamation-triangle",
				"Run stalled.",
				"Check the transcript and retry if it still needs work.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-warning-subtle text-warning-emphasis"),
			JobStatus.Paused => new JobOutcomeHintModel(
				"bi-chat-dots",
				"Waiting for input.",
				"Reply to continue the run.",
				"d-flex align-items-start gap-2 mt-2 px-2 py-2 rounded small bg-warning-subtle text-warning-emphasis"),
			_ => null
		};
	}

	public static bool HasDeliveredChanges(string? pullRequestUrl, string? gitCommitHash)
		=> !string.IsNullOrWhiteSpace(pullRequestUrl) || !string.IsNullOrWhiteSpace(gitCommitHash);

	public static bool HasDetectedChanges(int? changedFilesCount, string? pullRequestUrl, string? gitCommitHash)
		=> changedFilesCount.GetValueOrDefault() > 0 || HasDeliveredChanges(pullRequestUrl, gitCommitHash);

	public static string FormatPullRequestLabel(int? pullRequestNumber)
		=> pullRequestNumber.HasValue ? $"PR #{pullRequestNumber.Value}" : "PR ready";

	public static string FormatPullRequestReference(int? pullRequestNumber)
		=> pullRequestNumber.HasValue ? $"PR #{pullRequestNumber.Value}" : "Pull request";

	public static string FormatCommitLabel(string? gitCommitHash)
		=> string.IsNullOrWhiteSpace(gitCommitHash) ? "Commit" : $"Commit {FormatShortCommitHash(gitCommitHash)}";

	public static string FormatShortCommitHash(string? gitCommitHash)
		=> string.IsNullOrWhiteSpace(gitCommitHash)
			? string.Empty
			: gitCommitHash[..Math.Min(7, gitCommitHash.Length)];
}

internal sealed record JobOutcomeBadgeModel(string Icon, string Text, string Title, string CssClass);

internal sealed record JobOutcomeHintModel(string Icon, string Title, string Message, string ContainerClass);
