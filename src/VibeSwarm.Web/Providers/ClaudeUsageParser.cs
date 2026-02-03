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
			LimitType = UsageLimitType.SessionLimit,
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

		// Try to extract usage percentage
		var percentMatch = UsagePercentPattern().Match(stderr);
		if (percentMatch.Success && int.TryParse(percentMatch.Groups[1].Value, out var percent))
		{
			// Estimate current/max based on percentage
			// We don't know the actual max, so use 100 as a base
			limits.CurrentUsage = percent;
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
}
