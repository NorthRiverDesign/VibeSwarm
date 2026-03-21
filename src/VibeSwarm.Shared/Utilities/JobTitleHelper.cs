namespace VibeSwarm.Shared.Utilities;

public static class JobTitleHelper
{
	public static string? BuildSafeJobTitle(string? title, string? goalPrompt)
	{
		var source = string.IsNullOrWhiteSpace(title) ? goalPrompt : title;
		var normalized = source?.Trim();
		if (string.IsNullOrWhiteSpace(normalized))
		{
			return null;
		}

		return normalized.Length > 200 ? normalized[..197] + "..." : normalized;
	}

	public static bool ShouldSyncTitleWithGoalPrompt(string? title, string? goalPrompt)
	{
		var normalizedTitle = title?.Trim();
		if (string.IsNullOrWhiteSpace(normalizedTitle))
		{
			return true;
		}

		var promptDerivedTitle = BuildSafeJobTitle(null, goalPrompt);
		return string.Equals(normalizedTitle, promptDerivedTitle, StringComparison.Ordinal);
	}
}
