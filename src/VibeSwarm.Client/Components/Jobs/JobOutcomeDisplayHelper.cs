namespace VibeSwarm.Client.Components.Jobs;

internal static class JobOutcomeDisplayHelper
{
	public static bool HasDeliveredChanges(string? pullRequestUrl, string? gitCommitHash, DateTime? mergedAt = null)
		=> mergedAt.HasValue || !string.IsNullOrWhiteSpace(pullRequestUrl) || !string.IsNullOrWhiteSpace(gitCommitHash);

	public static bool HasDetectedChanges(int? changedFilesCount, string? pullRequestUrl, string? gitCommitHash, DateTime? mergedAt = null)
		=> changedFilesCount.GetValueOrDefault() > 0 || HasDeliveredChanges(pullRequestUrl, gitCommitHash, mergedAt);
}
