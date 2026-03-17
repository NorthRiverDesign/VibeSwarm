using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Parses Claude CLI stderr output for usage limit signals.
/// Claude CLI may report concurrent session, weekly, and monthly windows.
/// </summary>
public static partial class ClaudeUsageParser
{
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

	[GeneratedRegex(@"(?:try again in|wait|resets? (?:at|in))\s*(\d+)\s*(hour|minute|second|day)s?", RegexOptions.IgnoreCase)]
	private static partial Regex RelativeResetTimePattern();

	[GeneratedRegex(@"(\d+)\s*%\s*(?:of\s+)?(?:limit\s+)?(?:used|consumed|remaining)", RegexOptions.IgnoreCase)]
	private static partial Regex UsagePercentPattern();

	[GeneratedRegex(@"(session|weekly|daily|monthly)\s+limit[^\r\n]*?(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex LimitFractionPattern();

	[GeneratedRegex(@"(\d+)\s*/\s*(\d+)[^\r\n]*?(session|weekly|daily|monthly)\s+limit", RegexOptions.IgnoreCase)]
	private static partial Regex ReverseLimitFractionPattern();

	[GeneratedRegex(@"(\d+)\s*%\s+of\s+(?:your\s+)?(session|weekly|daily|monthly)\s+limit\s+(used|consumed|remaining)", RegexOptions.IgnoreCase)]
	private static partial Regex TypedUsagePercentPattern();

	[GeneratedRegex(@"(?:resets?|reset)\s+(session|weekly|daily|monthly)\s+(?:limit\s+)?(?:at|on|in)\s*([^\r\n.]+)", RegexOptions.IgnoreCase)]
	private static partial Regex ScopedResetPattern();

	[GeneratedRegex(@"(?:limit\s+)?(reached|exceeded)[^\r\n]*?(session|weekly|daily|monthly)", RegexOptions.IgnoreCase)]
	private static partial Regex ScopedReachedPattern();

	[GeneratedRegex(@"(session|weekly|daily|monthly)[^\r\n]*?(reached|exceeded)", RegexOptions.IgnoreCase)]
	private static partial Regex ReverseScopedReachedPattern();

	public static UsageLimits? ParseLimitSignals(string? stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
		{
			return null;
		}

		var stderrLower = stderr.ToLowerInvariant();
		if (!LimitPatterns.Any(pattern => stderrLower.Contains(pattern)))
		{
			return null;
		}

		var limitMessage = ExtractRelevantMessage(stderr);
		var windows = ExtractWindows(stderr);
		var genericResetTime = TryParseRelativeResetTime(stderr);
		if (genericResetTime.HasValue)
		{
			ApplyGenericResetTime(windows, genericResetTime.Value);
		}

		var isLimitReached = windows.Any(window => window.IsLimitReached)
			|| stderrLower.Contains("limit reached")
			|| stderrLower.Contains("quota exceeded")
			|| stderrLower.Contains("you've reached your usage limit");

		if (windows.Count == 0)
		{
			var percentMatch = UsagePercentPattern().Match(stderr);
			if (percentMatch.Success && int.TryParse(percentMatch.Groups[1].Value, out var percent))
			{
				var qualifier = percentMatch.Groups[0].Value.Contains("remaining", StringComparison.OrdinalIgnoreCase)
					? "remaining"
					: "used";
				windows.Add(new UsageLimitWindow
				{
					Scope = DetectScope(stderrLower),
					LimitType = DetectLimitType(stderrLower),
					CurrentUsage = qualifier == "remaining" ? 100 - percent : percent,
					MaxUsage = 100,
					ResetTime = genericResetTime,
					IsLimitReached = isLimitReached,
					Message = limitMessage
				});
			}
		}

		if (windows.Count == 0)
		{
			windows.Add(new UsageLimitWindow
			{
				Scope = DetectScope(stderrLower),
				LimitType = DetectLimitType(stderrLower),
				ResetTime = genericResetTime,
				IsLimitReached = isLimitReached,
				Message = limitMessage
			});
		}

		return UsageLimitWindowHelper.CreateUsageLimits(
			DetectLimitType(stderrLower),
			limitMessage,
			windows,
			isLimitReached);
	}

	public static bool ContainsLimitSignals(string? stderr)
	{
		if (string.IsNullOrWhiteSpace(stderr))
		{
			return false;
		}

		var stderrLower = stderr.ToLowerInvariant();
		return LimitPatterns.Any(pattern => stderrLower.Contains(pattern));
	}

	private static List<UsageLimitWindow> ExtractWindows(string stderr)
	{
		var windows = new List<UsageLimitWindow>();
		var scopedResetTimes = ExtractScopedResetTimes(stderr);
		var reachedScopes = ExtractReachedScopes(stderr);

		foreach (Match match in LimitFractionPattern().Matches(stderr))
		{
			if (!TryParseUsagePair(match.Groups[2].Value, match.Groups[3].Value, out var currentUsage, out var maxUsage))
			{
				continue;
			}

			var scope = ParseScope(match.Groups[1].Value);
			windows.Add(new UsageLimitWindow
			{
				Scope = scope,
				LimitType = ScopeToLimitType(scope),
				CurrentUsage = currentUsage,
				MaxUsage = maxUsage,
				ResetTime = scopedResetTimes.GetValueOrDefault(scope),
				IsLimitReached = reachedScopes.Contains(scope) || currentUsage >= maxUsage,
				Message = match.Value.Trim()
			});
		}

		foreach (Match match in ReverseLimitFractionPattern().Matches(stderr))
		{
			if (!TryParseUsagePair(match.Groups[1].Value, match.Groups[2].Value, out var currentUsage, out var maxUsage))
			{
				continue;
			}

			var scope = ParseScope(match.Groups[3].Value);
			windows.Add(new UsageLimitWindow
			{
				Scope = scope,
				LimitType = ScopeToLimitType(scope),
				CurrentUsage = currentUsage,
				MaxUsage = maxUsage,
				ResetTime = scopedResetTimes.GetValueOrDefault(scope),
				IsLimitReached = reachedScopes.Contains(scope) || currentUsage >= maxUsage,
				Message = match.Value.Trim()
			});
		}

		foreach (Match match in TypedUsagePercentPattern().Matches(stderr))
		{
			if (!int.TryParse(match.Groups[1].Value, out var percent))
			{
				continue;
			}

			var qualifier = match.Groups[3].Value.ToLowerInvariant();
			var scope = ParseScope(match.Groups[2].Value);
			windows.Add(new UsageLimitWindow
			{
				Scope = scope,
				LimitType = ScopeToLimitType(scope),
				CurrentUsage = qualifier == "remaining" ? 100 - percent : percent,
				MaxUsage = 100,
				ResetTime = scopedResetTimes.GetValueOrDefault(scope),
				IsLimitReached = reachedScopes.Contains(scope) || qualifier != "remaining" && percent >= 100,
				Message = match.Value.Trim()
			});
		}

		return UsageLimitWindowHelper.NormalizeWindows(windows);
	}

	private static Dictionary<UsageLimitWindowScope, DateTime?> ExtractScopedResetTimes(string stderr)
	{
		var scopedResetTimes = new Dictionary<UsageLimitWindowScope, DateTime?>();
		foreach (Match match in ScopedResetPattern().Matches(stderr))
		{
			var scope = ParseScope(match.Groups[1].Value);
			var parsed = TryParseResetValue(match.Groups[2].Value);
			if (parsed.HasValue)
			{
				scopedResetTimes[scope] = parsed;
			}
		}

		return scopedResetTimes;
	}

	private static HashSet<UsageLimitWindowScope> ExtractReachedScopes(string stderr)
	{
		var scopes = new HashSet<UsageLimitWindowScope>();

		foreach (Match match in ScopedReachedPattern().Matches(stderr))
		{
			scopes.Add(ParseScope(match.Groups[2].Value));
		}

		foreach (Match match in ReverseScopedReachedPattern().Matches(stderr))
		{
			scopes.Add(ParseScope(match.Groups[1].Value));
		}

		return scopes;
	}

	private static string ExtractRelevantMessage(string stderr)
	{
		return stderr
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(line => LimitPatterns.Any(pattern => line.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
			?? "Usage limit signal detected";
	}

	private static DateTime? TryParseRelativeResetTime(string stderr)
	{
		var resetMatch = RelativeResetTimePattern().Match(stderr);
		if (!resetMatch.Success || !int.TryParse(resetMatch.Groups[1].Value, out var amount))
		{
			return null;
		}

		var unit = resetMatch.Groups[2].Value.ToLowerInvariant();
		return unit switch
		{
			"second" or "seconds" => DateTime.UtcNow.AddSeconds(amount),
			"minute" or "minutes" => DateTime.UtcNow.AddMinutes(amount),
			"hour" or "hours" => DateTime.UtcNow.AddHours(amount),
			"day" or "days" => DateTime.UtcNow.AddDays(amount),
			_ => null
		};
	}

	private static DateTime? TryParseResetValue(string value)
	{
		var relativeValue = $"resets in {value.Trim()}";
		return TryParseRelativeResetTime(relativeValue);
	}

	private static void ApplyGenericResetTime(List<UsageLimitWindow> windows, DateTime genericResetTime)
	{
		if (windows.Count == 0)
		{
			return;
		}

		foreach (var window in windows.Where(window => !window.ResetTime.HasValue))
		{
			window.ResetTime = genericResetTime;
		}
	}

	private static bool TryParseUsagePair(string currentValue, string maxValue, out int currentUsage, out int maxUsage)
	{
		currentUsage = 0;
		maxUsage = 0;
		return int.TryParse(currentValue, out currentUsage)
			&& int.TryParse(maxValue, out maxUsage);
	}

	private static UsageLimitType DetectLimitType(string stderrLower)
	{
		if (stderrLower.Contains("weekly limit")
			|| stderrLower.Contains("daily limit")
			|| stderrLower.Contains("monthly limit")
			|| stderrLower.Contains("rate limit")
			|| stderrLower.Contains("rate limited"))
		{
			return UsageLimitType.RateLimit;
		}

		return UsageLimitType.SessionLimit;
	}

	private static UsageLimitWindowScope DetectScope(string stderrLower)
	{
		if (stderrLower.Contains("monthly limit"))
		{
			return UsageLimitWindowScope.Monthly;
		}
		if (stderrLower.Contains("weekly limit"))
		{
			return UsageLimitWindowScope.Weekly;
		}
		if (stderrLower.Contains("daily limit"))
		{
			return UsageLimitWindowScope.Daily;
		}
		if (stderrLower.Contains("session limit"))
		{
			return UsageLimitWindowScope.Session;
		}

		return UsageLimitWindowScope.Unknown;
	}

	private static UsageLimitWindowScope ParseScope(string label)
	{
		return label.ToLowerInvariant() switch
		{
			"session" => UsageLimitWindowScope.Session,
			"daily" => UsageLimitWindowScope.Daily,
			"weekly" => UsageLimitWindowScope.Weekly,
			"monthly" => UsageLimitWindowScope.Monthly,
			_ => UsageLimitWindowScope.Unknown
		};
	}

	private static UsageLimitType ScopeToLimitType(UsageLimitWindowScope scope)
	{
		return scope switch
		{
			UsageLimitWindowScope.Session => UsageLimitType.SessionLimit,
			UsageLimitWindowScope.Daily or UsageLimitWindowScope.Weekly or UsageLimitWindowScope.Monthly => UsageLimitType.RateLimit,
			_ => UsageLimitType.SessionLimit
		};
	}
}
