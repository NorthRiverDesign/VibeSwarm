namespace VibeSwarm.Shared.Utilities;

/// <summary>
/// Shared utility for generating summaries from execution output.
/// Used by both CLI and SDK providers to extract action-oriented statements.
/// </summary>
public static class OutputSummaryHelper
{
	/// <summary>
	/// Generates a summary from execution output by looking for action-oriented statements.
	/// </summary>
	public static string GenerateSummaryFromOutput(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
			return string.Empty;

		var lines = output.Split('\n');
		var significantActions = new List<string>();

		foreach (var line in lines)
		{
			var trimmed = line.Trim();

			// Skip empty lines and JSON
			if (string.IsNullOrWhiteSpace(trimmed)) continue;
			if (trimmed.StartsWith("{") || trimmed.StartsWith("[")) continue;
			if (trimmed.Length < 10) continue;

			// Look for action-oriented statements
			if (trimmed.Contains("created", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("modified", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("updated", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("added", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("removed", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("fixed", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("implemented", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("refactored", StringComparison.OrdinalIgnoreCase))
			{
				if (trimmed.Length < 200)
				{
					significantActions.Add(trimmed);
				}
			}
		}

		if (significantActions.Count > 0)
		{
			return string.Join("; ", significantActions.Take(3));
		}

		// Fallback: return first meaningful line
		foreach (var line in lines)
		{
			var trimmed = line.Trim();
			if (!string.IsNullOrWhiteSpace(trimmed) &&
				!trimmed.StartsWith("{") &&
				!trimmed.StartsWith("[") &&
				trimmed.Length >= 20 &&
				trimmed.Length <= 200)
			{
				return trimmed;
			}
		}

		return string.Empty;
	}
}
