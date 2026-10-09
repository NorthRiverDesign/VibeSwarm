using VibeSwarm.Shared.Data;

namespace VibeSwarm.Client.Components.Jobs;

internal static class JobOutcomeDisplayHelper
{
	public static IReadOnlyList<JobOutcomeBadgeModel> BuildBadges(
		JobStatus status,
		int? changedFilesCount,
		bool? buildVerified,
		bool buildVerificationEnabled,
		bool isPushed,
		string? gitCommitHash,
		string? pullRequestUrl,
		int? pullRequestNumber,
		DateTime? mergedAt)
	{
		var badges = new List<JobOutcomeBadgeModel>();
		var hasDetectedChanges = HasDetectedChanges(changedFilesCount, pullRequestUrl, gitCommitHash, mergedAt);
		var verificationMissing = status == JobStatus.Completed
			&& buildVerificationEnabled
			&& buildVerified is null
			&& hasDetectedChanges;

		if (status == JobStatus.Completed && changedFilesCount == 0)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-file-earmark",
				"No changes",
				"The job completed without any detected file changes.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-2 fg-1"));
		}
		else if (changedFilesCount.GetValueOrDefault() > 0)
		{
			var fileLabel = changedFilesCount == 1 ? "file changed" : "files changed";
			badges.Add(new JobOutcomeBadgeModel(
				"bi-files",
				$"{changedFilesCount} {fileLabel}",
				"The number of files changed by this job.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-2 fg-1"));
		}

		if (buildVerified == true)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-check2-circle",
				"Checks passed",
				"Configured build and test verification passed after the job finished.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-success fg-emphasis-success"));
		}
		else if (buildVerified == false)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-x-circle",
				"Checks failed",
				"Build or test verification failed after the job finished.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-danger fg-emphasis-danger"));
		}
		else if (verificationMissing)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-shield-exclamation",
				"Checks missing",
				"This project expects post-run verification, but this job did not record a verification result.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-warning fg-emphasis-warning"));
		}

		if (mergedAt.HasValue)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-check2-circle",
				"Merged",
				"The job's changes were merged into the target branch.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-success fg-emphasis-success"));
		}
		else if (isPushed)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-cloud-arrow-up",
				"Pushed",
				"The job's branch changes were pushed to the remote.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-info fg-emphasis-info"));
		}

		if (!string.IsNullOrWhiteSpace(pullRequestUrl))
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-github",
				FormatPullRequestLabel(pullRequestNumber),
				"A pull request was created for this job's changes.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-primary fg-emphasis-primary"));
		}
		else if (!string.IsNullOrWhiteSpace(gitCommitHash) && !mergedAt.HasValue)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-git",
				FormatCommitLabel(gitCommitHash),
				"The job's changes have been committed to git.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-info fg-emphasis-info"));
		}
		else if (hasDetectedChanges && status == JobStatus.Completed)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-send-check",
				"Ready to deliver",
				"The job produced changes, but they have not been committed or delivered yet.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-warning fg-emphasis-warning"));
		}
		else if (hasDetectedChanges && status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Stalled)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-exclamation-triangle",
				"Review changes",
				"The run stopped, but changes are still present for review.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-warning fg-emphasis-warning"));
		}

		if (status == JobStatus.Paused)
		{
			badges.Add(new JobOutcomeBadgeModel(
				"bi-chat-dots",
				"Waiting for input",
				"The job is paused until the user responds.",
				"d-inline-flex align-items-center gap-1 px-3 py-1 rounded-pill small bg-subtle-warning fg-emphasis-warning"));
		}

		return badges;
	}

	public static JobOutcomeHintModel? BuildHint(
		JobStatus status,
		int? changedFilesCount,
		bool? buildVerified,
		bool buildVerificationEnabled,
		bool isPushed,
		string? gitCommitHash,
		string? pullRequestUrl,
		int? pullRequestNumber,
		DateTime? mergedAt)
	{
		var hasDetectedChanges = HasDetectedChanges(changedFilesCount, pullRequestUrl, gitCommitHash, mergedAt);
		var pullRequestReference = FormatPullRequestReference(pullRequestNumber);
		var verificationMissing = status == JobStatus.Completed
			&& buildVerificationEnabled
			&& buildVerified is null
			&& hasDetectedChanges;

		return status switch
		{
			JobStatus.Completed when buildVerified == false => new JobOutcomeHintModel(
				"bi-shield-x",
				"Checks failed.",
				"Review the verification output before delivering these changes.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-danger fg-emphasis-danger"),
			JobStatus.Completed when verificationMissing => new JobOutcomeHintModel(
				"bi-shield-exclamation",
				"Checks missing.",
				"This project expects post-run verification, but this run did not record it.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-warning fg-emphasis-warning"),
			JobStatus.Completed when mergedAt.HasValue => new JobOutcomeHintModel(
				"bi-check2-circle",
				"Merged.",
				"The changes are already on the target branch, so only follow-up review remains.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-success fg-emphasis-success"),
			JobStatus.Completed when !string.IsNullOrWhiteSpace(pullRequestUrl) => new JobOutcomeHintModel(
				"bi-github",
				$"{pullRequestReference} ready.",
				"Review it and merge when the changes are approved.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-success fg-emphasis-success"),
			JobStatus.Completed when isPushed => new JobOutcomeHintModel(
				"bi-cloud-arrow-up",
				"Branch pushed.",
				"The remote branch is updated and ready for a pull request, merge, or branch review.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-info fg-emphasis-info"),
			JobStatus.Completed when !string.IsNullOrWhiteSpace(gitCommitHash) => new JobOutcomeHintModel(
				"bi-git",
				$"{FormatCommitLabel(gitCommitHash)} created.",
				"Push it or open a pull request when you are ready.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-primary fg-emphasis-primary"),
			JobStatus.Completed when changedFilesCount == 0 => new JobOutcomeHintModel(
				"bi-file-earmark",
				"No code changes.",
				"This run finished without any detected file modifications.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-2 fg-1"),
			JobStatus.Completed when buildVerified == true => new JobOutcomeHintModel(
				"bi-check2-circle",
				"Checks passed.",
				"Review the diff and finish delivery when it looks good.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-success fg-emphasis-success"),
			JobStatus.Completed when hasDetectedChanges => new JobOutcomeHintModel(
				"bi-send-check",
				"Changes ready.",
				"Review the diff and deliver them when you are satisfied.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-warning fg-emphasis-warning"),
			JobStatus.Failed or JobStatus.Cancelled or JobStatus.Stalled when hasDetectedChanges => new JobOutcomeHintModel(
				"bi-exclamation-triangle",
				"Working changes remain.",
				"Review them before retrying, committing, or discarding anything.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-warning fg-emphasis-warning"),
			JobStatus.Failed => new JobOutcomeHintModel(
				"bi-x-circle",
				"Run failed.",
				"Check the transcript before retrying.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-danger fg-emphasis-danger"),
			JobStatus.Cancelled => new JobOutcomeHintModel(
				"bi-slash-circle",
				"Run cancelled.",
				"It stopped before reaching a deliverable result.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-2 fg-1"),
			JobStatus.Stalled => new JobOutcomeHintModel(
				"bi-exclamation-triangle",
				"Run stalled.",
				"Check the transcript and retry if it still needs work.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-warning fg-emphasis-warning"),
			JobStatus.Paused => new JobOutcomeHintModel(
				"bi-chat-dots",
				"Waiting for input.",
				"Reply to continue the run.",
				"d-flex align-items-start gap-3 mt-3 px-3 py-3 rounded small bg-subtle-warning fg-emphasis-warning"),
			_ => null
		};
	}

	public static bool HasDeliveredChanges(string? pullRequestUrl, string? gitCommitHash, DateTime? mergedAt = null)
		=> mergedAt.HasValue || !string.IsNullOrWhiteSpace(pullRequestUrl) || !string.IsNullOrWhiteSpace(gitCommitHash);

	public static bool HasDetectedChanges(int? changedFilesCount, string? pullRequestUrl, string? gitCommitHash, DateTime? mergedAt = null)
		=> changedFilesCount.GetValueOrDefault() > 0 || HasDeliveredChanges(pullRequestUrl, gitCommitHash, mergedAt);

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

	public static string? FormatListSessionSummary(string? sessionSummary)
	{
		if (string.IsNullOrWhiteSpace(sessionSummary))
		{
			return null;
		}

		var firstLine = sessionSummary
			.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault();

		if (string.IsNullOrWhiteSpace(firstLine))
		{
			return null;
		}

		return firstLine.TrimStart('-', '*', '+', '\u2022', ' ').Trim();
	}
}

internal sealed record JobOutcomeBadgeModel(string Icon, string Text, string Title, string CssClass);

internal sealed record JobOutcomeHintModel(string Icon, string Title, string Message, string ContainerClass);
