using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Parses Claude CLI stderr output for usage limit signals.
/// Claude CLI may output limit-related messages when approaching or reaching limits.
/// </summary>
public static partial class ClaudeUsageParser
{
	/// <summary>
	/// Known limit signal patterns in Claude CLI output
	/// </summary>
	private static readonly string[] LimitPatterns =
	[
		"you've reached your usage limit",
		"rate limit",
		"usage limit",
		"please wait",
		"try again",
		"limit reached",
		"session limit",
		"rate limited",
		"too many requests",
		"quota exceeded",
		"daily limit",
		"weekly limit",
		"monthly limit"
	];

	/// <summary>
	/// Regex to extract time-based reset information
	/// Examples: "try again in 2 hours", "resets at 3:00 PM", "wait 30 minutes"
	/// </summary>
	[GeneratedRegex(@"(?:try again in|wait|resets? (?:at|in))\s*(\d+)\s*(hour|minute|second|day)s?", RegexOptions.IgnoreCase)]
	private static partial Regex ResetTimePattern();

	/// <summary>
	/// Regex to extract usage percentage
	/// Examples: "80% of limit used", "used 90%"
	/// </summary>
	[GeneratedRegex(@"(\d+)\s*%\s*(?:of\s+)?(?:limit\s+)?(?:used|consumed|remaining)", RegexOptions.IgnoreCase)]
	private static partial Regex UsagePercentPattern();

	[GeneratedRegex(@"(session|weekly|daily|monthly)\s+limit[^\r\n]*?(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex LimitFractionPattern();

	[GeneratedRegex(@"(\d+)\s*/\s*(\d+)[^\r\n]*?(session|weekly|daily|monthly)\s+limit", RegexOptions.IgnoreCase)]
	private static partial Regex ReverseLimitFractionPattern();

	[GeneratedRegex(@"(\d+)\s*%\s+of\s+(?:your\s+)?(session|weekly|daily|monthly)\s+limit\s+(used|consumed|remaining)", RegexOptions.IgnoreCase)]
	private static partial Regex TypedUsagePercentPattern();

	/// <summary>
	/// Parses Claude CLI stderr for usage limit signals.
	/// </summary>
	/// <param name="stderr">The stderr output from Claude CLI</param>
	/// <returns>UsageLimits if limit signals were detected, null otherwise</returns>
	public static UsageLimits? ParseLimitSignals(string? stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
			return null;

		var stderrLower = stderr.ToLowerInvariant();

		// Check if any limit patterns are present
		var hasLimitSignal = false;
		var isLimitReached = false;
		string? limitMessage = null;

		foreach (var pattern in LimitPatterns)
		{
			if (stderrLower.Contains(pattern))
			{
				hasLimitSignal = true;

				// Determine if this is an actual limit reached vs approaching
				if (pattern.Contains("reached") || pattern.Contains("exceeded") || pattern.Contains("limit reached"))
				{
					isLimitReached = true;
				}

				// Extract the relevant line as the message
				var lines = stderr.Split('\n');
				foreach (var line in lines)
				{
					if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
					{
						limitMessage = line.Trim();
						break;
					}
				}

				break;
			}
		}

		if (!hasLimitSignal)
			return null;

		var limits = new UsageLimits
		{
			LimitType = DetectLimitType(stderrLower),
			IsLimitReached = isLimitReached,
			Message = limitMessage ?? "Usage limit signal detected"
		};

		// Try to extract reset time
		var resetMatch = ResetTimePattern().Match(stderr);
		if (resetMatch.Success && int.TryParse(resetMatch.Groups[1].Value, out var amount))
		{
			var unit = resetMatch.Groups[2].Value.ToLowerInvariant();
			var resetTime = unit switch
			{
				"second" or "seconds" => DateTime.UtcNow.AddSeconds(amount),
				"minute" or "minutes" => DateTime.UtcNow.AddMinutes(amount),
				"hour" or "hours" => DateTime.UtcNow.AddHours(amount),
				"day" or "days" => DateTime.UtcNow.AddDays(amount),
				_ => (DateTime?)null
			};
			limits.ResetTime = resetTime;
		}

		var fractionMatch = LimitFractionPattern().Match(stderr);
		if (fractionMatch.Success &&
			int.TryParse(fractionMatch.Groups[2].Value, out var typedCurrentUsage) &&
			int.TryParse(fractionMatch.Groups[3].Value, out var typedMaxUsage))
		{
			limits.CurrentUsage = typedCurrentUsage;
			limits.MaxUsage = typedMaxUsage;
			limits.LimitType = ParseLimitTypeLabel(fractionMatch.Groups[1].Value);
			return limits;
		}

		var reverseFractionMatch = ReverseLimitFractionPattern().Match(stderr);
		if (reverseFractionMatch.Success &&
			int.TryParse(reverseFractionMatch.Groups[1].Value, out typedCurrentUsage) &&
			int.TryParse(reverseFractionMatch.Groups[2].Value, out typedMaxUsage))
		{
			limits.CurrentUsage = typedCurrentUsage;
			limits.MaxUsage = typedMaxUsage;
			limits.LimitType = ParseLimitTypeLabel(reverseFractionMatch.Groups[3].Value);
			return limits;
		}

		var typedPercentMatch = TypedUsagePercentPattern().Match(stderr);
		if (typedPercentMatch.Success && int.TryParse(typedPercentMatch.Groups[1].Value, out var typedPercent))
		{
			var qualifier = typedPercentMatch.Groups[3].Value.ToLowerInvariant();
			limits.CurrentUsage = qualifier == "remaining" ? 100 - typedPercent : typedPercent;
			limits.MaxUsage = 100;
			limits.LimitType = ParseLimitTypeLabel(typedPercentMatch.Groups[2].Value);
			return limits;
		}

		// Try to extract usage percentage
		var percentMatch = UsagePercentPattern().Match(stderr);
		if (percentMatch.Success && int.TryParse(percentMatch.Groups[1].Value, out var percent))
		{
			var qualifier = percentMatch.Groups[0].Value.Contains("remaining", StringComparison.OrdinalIgnoreCase)
				? "remaining"
				: "used";
			limits.CurrentUsage = qualifier == "remaining" ? 100 - percent : percent;
			limits.MaxUsage = 100;
		}

		return limits;
	}

	/// <summary>
	/// Checks if stderr contains any limit-related signals without full parsing.
	/// Useful for quick checks before doing full parsing.
	/// </summary>
	public static bool ContainsLimitSignals(string? stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
			return false;

		var stderrLower = stderr.ToLowerInvariant();
		return LimitPatterns.Any(pattern => stderrLower.Contains(pattern));
	}

	private static UsageLimitType DetectLimitType(string stderrLower)
	{
		if (stderrLower.Contains("weekly limit") ||
			stderrLower.Contains("daily limit") ||
			stderrLower.Contains("monthly limit") ||
			stderrLower.Contains("rate limit") ||
			stderrLower.Contains("rate limited"))
		{
			return UsageLimitType.RateLimit;
		}

		return UsageLimitType.SessionLimit;
	}

	private static UsageLimitType ParseLimitTypeLabel(string label)
	{
		var normalized = label.ToLowerInvariant();
		return normalized switch
		{
			"weekly" or "daily" or "monthly" => UsageLimitType.RateLimit,
			_ => UsageLimitType.SessionLimit
		};
	}
}
