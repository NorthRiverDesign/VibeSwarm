using System.Globalization;
using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Parses GitHub Copilot CLI output for usage, premium request budgets, and estimated cost signals.
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

	[GeneratedRegex(@"(?:Est\.?|Estimated)\s*cost[^$0-9\r\n]*\$?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
	private static partial Regex EstimatedCostPattern();

	[GeneratedRegex(@"\(\s*\$?\s*(\d+(?:\.\d+)?)\s*\)", RegexOptions.IgnoreCase)]
	private static partial Regex InlineCostPattern();

	[GeneratedRegex(@"resets?\s+(?:on|at|in)\s*([^\r\n.]+)", RegexOptions.IgnoreCase)]
	private static partial Regex ResetPattern();

	[GeneratedRegex(@"(\d+)\s*(hour|minute|second|day)s?", RegexOptions.IgnoreCase)]
	private static partial Regex RelativeResetPattern();

	public static void ApplyToExecutionResult(string? stderr, ExecutionResult result)
	{
		if (string.IsNullOrWhiteSpace(stderr))
		{
			return;
		}

		ParseTokenUsage(stderr, result);
		ParseModel(stderr, result);
		ParsePremiumRequestsConsumed(stderr, result);
		ParseEstimatedCost(stderr, result);

		var limits = ParseLimitSignals(stderr);
		if (limits != null)
		{
			result.DetectedUsageLimits = limits;
		}
	}

	public static UsageLimits? ParseLimitSignals(string? stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
		{
			return null;
		}

		var matchedLine = ExtractBudgetLine(stderr);
		var resetTime = TryParseResetTime(stderr);
		var windows = new List<UsageLimitWindow>();
		var matched = false;

		var fractionMatch = PremiumBudgetFractionPattern().Match(stderr);
		if (fractionMatch.Success
			&& int.TryParse(fractionMatch.Groups[1].Value, out var currentUsage)
			&& int.TryParse(fractionMatch.Groups[2].Value, out var maxUsage))
		{
			windows.Add(CreateMonthlyWindow(currentUsage, maxUsage, resetTime, matchedLine, currentUsage >= maxUsage));
			matched = true;
		}

		if (!matched)
		{
			var ofMatch = PremiumBudgetOfPattern().Match(stderr);
			if (ofMatch.Success
				&& int.TryParse(ofMatch.Groups[1].Value, out currentUsage)
				&& int.TryParse(ofMatch.Groups[2].Value, out maxUsage))
			{
				windows.Add(CreateMonthlyWindow(currentUsage, maxUsage, resetTime, matchedLine, currentUsage >= maxUsage));
				matched = true;
			}
		}

		if (!matched)
		{
			var percentMatch = PremiumBudgetPercentPattern().Match(stderr);
			if (percentMatch.Success && int.TryParse(percentMatch.Groups[1].Value, out var percent))
			{
				var qualifier = percentMatch.Groups[2].Value.ToLowerInvariant();
				windows.Add(CreateMonthlyWindow(
					qualifier == "remaining" ? 100 - percent : percent,
					100,
					resetTime,
					matchedLine ?? percentMatch.Value.Trim(),
					qualifier != "remaining" && percent >= 100));
				matched = true;
			}
		}

		if (PremiumBudgetReachedPattern().IsMatch(stderr))
		{
			if (windows.Count == 0)
			{
				windows.Add(CreateMonthlyWindow(null, null, resetTime, matchedLine, true));
			}
			else
			{
				foreach (var window in windows)
				{
					window.IsLimitReached = true;
					if (window.CurrentUsage.HasValue && window.MaxUsage.HasValue && window.CurrentUsage < window.MaxUsage)
					{
						window.CurrentUsage = window.MaxUsage;
					}
				}
			}

			matched = true;
		}

		return matched
			? UsageLimitWindowHelper.CreateUsageLimits(
				UsageLimitType.PremiumRequests,
				matchedLine,
				windows,
				windows.Any(window => window.IsLimitReached))
			: null;
	}

	private static void ParseTokenUsage(string stderr, ExecutionResult result)
	{
		var match = TokenUsagePattern().Match(stderr);
		if (!match.Success)
		{
			return;
		}

		if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var inputValue))
		{
			result.InputTokens = match.Groups[2].Success
				? (int)(inputValue * 1000)
				: (int)inputValue;
		}

		if (double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var outputValue))
		{
			result.OutputTokens = match.Groups[4].Success
				? (int)(outputValue * 1000)
				: (int)outputValue;
		}
	}

	private static void ParseModel(string stderr, ExecutionResult result)
	{
		if (!string.IsNullOrEmpty(result.ModelUsed))
		{
			return;
		}

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

	private static void ParseEstimatedCost(string stderr, ExecutionResult result)
	{
		if (result.CostUsd.HasValue)
		{
			return;
		}

		var estimatedCostMatch = EstimatedCostPattern().Match(stderr);
		if (estimatedCostMatch.Success
			&& decimal.TryParse(estimatedCostMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var estimatedCost))
		{
			result.CostUsd = estimatedCost;
			return;
		}

		var inlineCostMatch = InlineCostPattern().Match(stderr);
		if (inlineCostMatch.Success
			&& decimal.TryParse(inlineCostMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var inlineCost))
		{
			result.CostUsd = inlineCost;
		}
	}

	private static DateTime? TryParseResetTime(string stderr)
	{
		var match = ResetPattern().Match(stderr);
		if (!match.Success)
		{
			return null;
		}

		var rawValue = match.Groups[1].Value.Trim();
		if (DateTime.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var absoluteReset))
		{
			return absoluteReset;
		}

		var relativeMatch = RelativeResetPattern().Match(rawValue);
		if (!relativeMatch.Success || !int.TryParse(relativeMatch.Groups[1].Value, out var amount))
		{
			return null;
		}

		return relativeMatch.Groups[2].Value.ToLowerInvariant() switch
		{
			"second" or "seconds" => DateTime.UtcNow.AddSeconds(amount),
			"minute" or "minutes" => DateTime.UtcNow.AddMinutes(amount),
			"hour" or "hours" => DateTime.UtcNow.AddHours(amount),
			"day" or "days" => DateTime.UtcNow.AddDays(amount),
			_ => null
		};
	}

	private static UsageLimitWindow CreateMonthlyWindow(
		int? currentUsage,
		int? maxUsage,
		DateTime? resetTime,
		string? message,
		bool isLimitReached)
	{
		return new UsageLimitWindow
		{
			Scope = UsageLimitWindowScope.Monthly,
			LimitType = UsageLimitType.PremiumRequests,
			CurrentUsage = currentUsage,
			MaxUsage = maxUsage,
			ResetTime = resetTime,
			IsLimitReached = isLimitReached,
			Message = message
		};
	}

	private static string? ExtractBudgetLine(string stderr)
	{
		return stderr
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(line =>
				line.Contains("premium request", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("premium budget", StringComparison.OrdinalIgnoreCase)
				|| line.Contains("remaining requests", StringComparison.OrdinalIgnoreCase));
	}
}
