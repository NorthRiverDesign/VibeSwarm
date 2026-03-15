using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Parses GitHub Copilot CLI output for usage and premium request budget signals.
/// </summary>
public static partial class CopilotUsageParser
{
	[GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(k)?\s*in\s*,\s*(\d+(?:\.\d+)?)\s*(k)?\s*out", RegexOptions.IgnoreCase)]
	private static partial Regex TokenUsagePattern();

	[GeneratedRegex(@"^\s*(?:\[ERR\])?\s*(claude-[\w.-]+|gpt-[\w.-]+|gemini-[\w.-]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
	private static partial Regex ModelPattern();

	[GeneratedRegex(@"Est\.\s*(\d+)\s*Premium\s*requests?", RegexOptions.IgnoreCase)]
	private static partial Regex PremiumRequestsConsumedPattern();

	[GeneratedRegex(@"(?:(?:premium\s+requests?|premium\s+request\s+budget)[^0-9\r\n]*|used[^0-9\r\n]*)(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex PremiumBudgetFractionPattern();

	[GeneratedRegex(@"(\d+)\s+of\s+(\d+)\s+premium\s+requests?\s+(?:used|consumed)", RegexOptions.IgnoreCase)]
	private static partial Regex PremiumBudgetOfPattern();

	[GeneratedRegex(@"(\d+)\s*%\s+of\s+(?:your\s+)?premium\s+request(?:s)?\s+(?:budget|limit)\s+(used|consumed|remaining)", RegexOptions.IgnoreCase)]
	private static partial Regex PremiumBudgetPercentPattern();

	[GeneratedRegex(@"premium\s+request(?:s)?\s+(?:limit|budget)[^\r\n]*(reached|exceeded)", RegexOptions.IgnoreCase)]
	private static partial Regex PremiumBudgetReachedPattern();

	/// <summary>
	/// Populates usage-related fields on an execution result from Copilot CLI stderr.
	/// </summary>
	public static void ApplyToExecutionResult(string? stderr, ExecutionResult result)
	{
		if (string.IsNullOrWhiteSpace(stderr))
			return;

		ParseTokenUsage(stderr, result);
		ParseModel(stderr, result);
		ParsePremiumRequestsConsumed(stderr, result);

		var limits = ParseLimitSignals(stderr);
		if (limits != null)
		{
			result.DetectedUsageLimits = limits;
		}
	}

	/// <summary>
	/// Parses premium request budget information from Copilot CLI stderr.
	/// </summary>
	public static UsageLimits? ParseLimitSignals(string? stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
			return null;

		var matchedLine = ExtractBudgetLine(stderr);
		var limits = new UsageLimits
		{
			LimitType = UsageLimitType.PremiumRequests,
			Message = matchedLine
		};

		var matched = false;

		var fractionMatch = PremiumBudgetFractionPattern().Match(stderr);
		if (fractionMatch.Success &&
			int.TryParse(fractionMatch.Groups[1].Value, out var currentUsage) &&
			int.TryParse(fractionMatch.Groups[2].Value, out var maxUsage))
		{
			limits.CurrentUsage = currentUsage;
			limits.MaxUsage = maxUsage;
			matched = true;
		}

		if (!matched)
		{
			var ofMatch = PremiumBudgetOfPattern().Match(stderr);
			if (ofMatch.Success &&
				int.TryParse(ofMatch.Groups[1].Value, out currentUsage) &&
				int.TryParse(ofMatch.Groups[2].Value, out maxUsage))
			{
				limits.CurrentUsage = currentUsage;
				limits.MaxUsage = maxUsage;
				matched = true;
			}
		}

		if (!matched)
		{
			var percentMatch = PremiumBudgetPercentPattern().Match(stderr);
			if (percentMatch.Success && int.TryParse(percentMatch.Groups[1].Value, out var percent))
			{
				var qualifier = percentMatch.Groups[2].Value.ToLowerInvariant();
				limits.CurrentUsage = qualifier == "remaining" ? 100 - percent : percent;
				limits.MaxUsage = 100;
				matched = true;
			}
		}

		if (PremiumBudgetReachedPattern().IsMatch(stderr))
		{
			limits.IsLimitReached = true;
			matched = true;

			if (limits.CurrentUsage.HasValue && limits.MaxUsage.HasValue && limits.CurrentUsage < limits.MaxUsage)
			{
				limits.CurrentUsage = limits.MaxUsage;
			}
		}

		return matched ? limits : null;
	}

	private static void ParseTokenUsage(string stderr, ExecutionResult result)
	{
		var match = TokenUsagePattern().Match(stderr);
		if (!match.Success)
			return;

		if (double.TryParse(match.Groups[1].Value, out var inputValue))
		{
			result.InputTokens = match.Groups[2].Success
				? (int)(inputValue * 1000)
				: (int)inputValue;
		}

		if (double.TryParse(match.Groups[3].Value, out var outputValue))
		{
			result.OutputTokens = match.Groups[4].Success
				? (int)(outputValue * 1000)
				: (int)outputValue;
		}
	}

	private static void ParseModel(string stderr, ExecutionResult result)
	{
		if (!string.IsNullOrEmpty(result.ModelUsed))
			return;

		var modelMatch = ModelPattern().Match(stderr);
		if (modelMatch.Success)
		{
			result.ModelUsed = modelMatch.Groups[1].Value.Trim();
		}
	}

	private static void ParsePremiumRequestsConsumed(string stderr, ExecutionResult result)
	{
		var premiumMatch = PremiumRequestsConsumedPattern().Match(stderr);
		if (premiumMatch.Success && int.TryParse(premiumMatch.Groups[1].Value, out var premiumRequests))
		{
			result.PremiumRequestsConsumed = premiumRequests;
		}
	}

	private static string? ExtractBudgetLine(string stderr)
	{
		return stderr
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(line =>
				line.Contains("premium request", StringComparison.OrdinalIgnoreCase) ||
				line.Contains("premium budget", StringComparison.OrdinalIgnoreCase));
	}
}
