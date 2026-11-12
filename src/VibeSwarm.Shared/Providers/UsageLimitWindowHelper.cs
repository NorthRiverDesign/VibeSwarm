namespace VibeSwarm.Shared.Providers;

public static class UsageLimitWindowHelper
{
	public static List<UsageLimitWindow> NormalizeWindows(IEnumerable<UsageLimitWindow>? windows)
	{
		// Label is part of the identity, not decoration: a provider can report two windows
		// over the same horizon that differ only by name, such as weekly with and without
		// overage. Grouping on scope alone silently merged them into one meter.
		return (windows ?? [])
			.Where(window => window != null)
			.GroupBy(window => new { window.Scope, window.LimitType, window.Label })
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
					Label = group.Key.Label,
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

	/// <summary>
	/// Combines an earlier usage reading with a later one. A window the later reading reports
	/// replaces the same window in the earlier one, so a run that reports its usage several times
	/// ends on its last figure, not its first. Windows only the earlier reading carries are kept.
	/// </summary>
	public static UsageLimits Merge(UsageLimits? existing, UsageLimits latest)
	{
		if (existing == null)
		{
			return latest;
		}

		var latestWindows = NormalizeWindows(latest.Windows);
		var olderWindows = NormalizeWindows(existing.Windows)
			.Where(window => !latestWindows.Any(newer => IsSameWindow(newer, window)));
		var windows = NormalizeWindows(latestWindows.Concat(olderWindows));
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

	/// <summary>
	/// The window as it stands at <paramref name="utcNow"/>. Once it has reset, none of it is used
	/// any more, whatever the last reading said, so it shows as empty until a new reading arrives.
	/// The reset time stays so the reader can tell when that happened.
	/// </summary>
	public static UsageLimitWindow AsOf(UsageLimitWindow window, DateTime utcNow)
	{
		if (!window.HasResetBy(utcNow))
		{
			return window;
		}

		return new UsageLimitWindow
		{
			Scope = window.Scope,
			LimitType = window.LimitType,
			Label = window.Label,
			CurrentUsage = window.CurrentUsage.HasValue ? 0 : null,
			MaxUsage = window.MaxUsage,
			ResetTime = window.ResetTime,
			IsLimitReached = false
		};
	}

	/// <summary>Same identity as <see cref="NormalizeWindows"/> groups on.</summary>
	private static bool IsSameWindow(UsageLimitWindow left, UsageLimitWindow right)
		=> left.Scope == right.Scope
			&& left.LimitType == right.LimitType
			&& string.Equals(left.Label, right.Label, StringComparison.Ordinal);

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
