using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Providers.Copilot;

/// <summary>
/// Parses model metadata from Copilot CLI output.
/// </summary>
public static partial class CopilotModelParser
{
	/// <summary>
	/// Regex to extract the comma-separated model list from the --model validation error.
	/// Example: "argument 'invalid' is invalid. Allowed choices are claude-sonnet-4.6, gpt-5.4, ..."
	/// </summary>
	[GeneratedRegex(@"Allowed\s+choices\s+are\s+(.+?)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
	private static partial Regex AllowedChoicesPattern();

	/// <summary>
	/// Parses model IDs from the error message produced by passing an invalid model to --model.
	/// The CLI outputs: "error: option '--model' argument 'X' is invalid. Allowed choices are model1, model2, ..."
	/// </summary>
	public static List<string>? ParseModelChoicesFromError(string? stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
			return null;

		var match = AllowedChoicesPattern().Match(stderr);
		if (!match.Success)
			return null;

		var choicesText = match.Groups[1].Value;
		var models = choicesText
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(m => !string.IsNullOrWhiteSpace(m))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		return models.Count > 0 ? models : null;
	}
}
