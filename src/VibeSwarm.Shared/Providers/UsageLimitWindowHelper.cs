namespace VibeSwarm.Shared.Providers;

public static class UsageLimitWindowHelper
{
	public static List<UsageLimitWindow> NormalizeWindows(IEnumerable<UsageLimitWindow>? windows)
	{
		return (windows ?? [])
			.Where(window => window != null)
			.GroupBy(window => new { window.Scope, window.LimitType })
			.Select(group =>
			{
				var windowsInGroup = group.ToList();
				var mostComplete = windowsInGroup
					.OrderByDescending(window => GetCompletenessScore(window))
					.First();

				return new UsageLimitWindow
				{
					Scope = group.Key.Scope,
					LimitType = group.Key.LimitType,
					IsLimitReached = windowsInGroup.Any(window => window.IsLimitReached),
					CurrentUsage = mostComplete.CurrentUsage,
					MaxUsage = mostComplete.MaxUsage,
					ResetTime = mostComplete.ResetTime ?? windowsInGroup.FirstOrDefault(window => window.ResetTime.HasValue)?.ResetTime,
					Message = mostComplete.Message ?? windowsInGroup.FirstOrDefault(window => !string.IsNullOrWhiteSpace(window.Message))?.Message
				};
			})
			.OrderByDescending(window => GetPriority(window))
			.ToList();
	}

	public static UsageLimitWindow? SelectPrimaryWindow(IEnumerable<UsageLimitWindow>? windows)
	{
		return NormalizeWindows(windows)
			.OrderByDescending(window => GetPriority(window))
			.FirstOrDefault();
	}

	public static UsageLimits CreateUsageLimits(
		UsageLimitType fallbackType,
		string? message,
		IEnumerable<UsageLimitWindow>? windows,
		bool isLimitReached = false)
	{
		var normalizedWindows = NormalizeWindows(windows);
		var primaryWindow = SelectPrimaryWindow(normalizedWindows);

		return new UsageLimits
		{
			LimitType = primaryWindow?.LimitType ?? fallbackType,
			IsLimitReached = isLimitReached || normalizedWindows.Any(window => window.IsLimitReached),
			CurrentUsage = primaryWindow?.CurrentUsage,
			MaxUsage = primaryWindow?.MaxUsage,
			ResetTime = primaryWindow?.ResetTime,
			Message = primaryWindow?.Message ?? message,
			Windows = normalizedWindows
		};
	}

	public static UsageLimits Merge(UsageLimits? existing, UsageLimits latest)
	{
		if (existing == null)
		{
			return latest;
		}

		var windows = NormalizeWindows(existing.Windows.Concat(latest.Windows));
		var primaryWindow = SelectPrimaryWindow(windows);

		return new UsageLimits
		{
			LimitType = primaryWindow?.LimitType
				?? (latest.LimitType != UsageLimitType.None ? latest.LimitType : existing.LimitType),
			IsLimitReached = latest.IsLimitReached || existing.IsLimitReached || windows.Any(window => window.IsLimitReached),
			CurrentUsage = primaryWindow?.CurrentUsage ?? latest.CurrentUsage ?? existing.CurrentUsage,
			MaxUsage = primaryWindow?.MaxUsage ?? latest.MaxUsage ?? existing.MaxUsage,
			ResetTime = primaryWindow?.ResetTime ?? latest.ResetTime ?? existing.ResetTime,
			Message = primaryWindow?.Message
				?? (string.IsNullOrWhiteSpace(latest.Message) ? existing.Message : latest.Message),
			Windows = windows
		};
	}

	private static int GetPriority(UsageLimitWindow window)
	{
		var percentUsed = window.PercentUsed ?? 0;
		var scopePriority = window.Scope switch
		{
			UsageLimitWindowScope.Session => 40,
			UsageLimitWindowScope.Daily => 30,
			UsageLimitWindowScope.Weekly => 20,
			UsageLimitWindowScope.Monthly => 10,
			_ => 0
		};

		return (window.IsLimitReached ? 10_000 : 0)
			+ (percentUsed * 10)
			+ scopePriority
			+ GetCompletenessScore(window);
	}

	private static int GetCompletenessScore(UsageLimitWindow window)
	{
		var score = 0;
		if (window.CurrentUsage.HasValue)
		{
			score += 4;
		}
		if (window.MaxUsage.HasValue)
		{
			score += 4;
		}
		if (window.ResetTime.HasValue)
		{
			score += 2;
		}
		if (!string.IsNullOrWhiteSpace(window.Message))
		{
			score += 1;
		}
		return score;
	}
}
