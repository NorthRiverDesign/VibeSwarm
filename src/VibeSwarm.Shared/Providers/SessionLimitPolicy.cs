namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Decides when a provider's usage is too close to its limit to start another job: its session,
/// the short rolling window such as Claude's five hours, or a longer one such as its weekly limit.
/// </summary>
/// <remarks>
/// A job that runs into the limit midway stops with its work half done. Holding new jobs back
/// a little early and resuming once the window resets costs only waiting time. Holding back
/// before a weekly limit runs out also leaves the user some of their plan, and keeps unattended
/// jobs from running into paid overage.
/// </remarks>
public static class SessionLimitPolicy
{
	public const int DefaultPauseThresholdPercent = 90;
	public const int MinPauseThresholdPercent = 50;
	public const int MaxPauseThresholdPercent = 100;

	/// <summary>
	/// How many recent jobs the expected per-job usage is averaged over.
	/// </summary>
	private const int EstimateSampleSize = 10;

	/// <summary>
	/// Two snapshots belong to the same session when their reset times are this close.
	/// </summary>
	private static readonly TimeSpan SameSessionTolerance = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Whether this provider type meters usage in sessions, so the pause threshold applies.
	/// </summary>
	public static bool HasSessionLimit(ProviderType providerType)
		=> providerType == ProviderType.Claude;

	/// <summary>
	/// Whether a window is one the policy guards: a session, or a daily, weekly or monthly limit.
	/// </summary>
	public static bool IsGuardedScope(UsageLimitWindowScope scope)
		=> scope is UsageLimitWindowScope.Session
			or UsageLimitWindowScope.Daily
			or UsageLimitWindowScope.Weekly
			or UsageLimitWindowScope.Monthly;

	public static int ResolvePauseThreshold(int? configuredPercent)
		=> configuredPercent is int percent
			? Math.Clamp(percent, MinPauseThresholdPercent, MaxPauseThresholdPercent)
			: DefaultPauseThresholdPercent;

	/// <summary>
	/// Returns the hold that should keep jobs from starting, or null when every window has room.
	/// A session holds jobs once its usage reaches <paramref name="pauseThresholdPercent"/>, or
	/// earlier (from half used) when one more typical job would use up what is left. A daily,
	/// weekly or monthly limit holds them once it reaches <paramref name="longWindowPauseThresholdPercent"/>.
	/// When several windows hold, jobs wait for the one that resets last.
	/// </summary>
	public static SessionLimitHold? Evaluate(
		IEnumerable<UsageLimitWindow>? windows,
		int pauseThresholdPercent,
		int? expectedJobUsagePercent,
		DateTime utcNow,
		int longWindowPauseThresholdPercent = DefaultPauseThresholdPercent)
	{
		SessionLimitHold? hold = null;
		foreach (var window in windows ?? [])
		{
			// A window whose reset time has passed has already started over, and one without a
			// reset time gives no point to wait for.
			if (window == null
				|| !IsGuardedScope(window.Scope)
				|| window.ResetTime is not DateTime resetTime
				|| resetTime <= utcNow)
			{
				continue;
			}

			if (window.Scope != UsageLimitWindowScope.Session)
			{
				var longWindowUsed = window.IsLimitReached
					? Math.Max(window.PercentUsed ?? 100, 100)
					: window.PercentUsed;
				if (longWindowUsed is int longUsed
					&& longUsed >= longWindowPauseThresholdPercent
					&& (hold == null || resetTime > hold.Until))
				{
					hold = new SessionLimitHold(resetTime, longUsed, longWindowPauseThresholdPercent, null, window.Scope);
				}

				continue;
			}

			var percentUsed = window.IsLimitReached
				? Math.Max(window.PercentUsed ?? 100, 100)
				: window.PercentUsed;
			if (percentUsed is not int used)
			{
				continue;
			}

			// The estimate can only bring the pause forward to half a session. Jobs that use
			// more than a whole session would otherwise never be allowed to start.
			var nextJobWouldReachLimit = expectedJobUsagePercent is int expected
				&& expected > 0
				&& used >= MinPauseThresholdPercent
				&& used + expected >= 100;
			if (used < pauseThresholdPercent && !nextJobWouldReachLimit)
			{
				continue;
			}

			if (hold == null || resetTime > hold.Until)
			{
				hold = new SessionLimitHold(resetTime, used, pauseThresholdPercent, expectedJobUsagePercent);
			}
		}

		return hold;
	}

	/// <summary>
	/// Estimates how much of a session one job uses, from the session usage reported by recent
	/// jobs. Consecutive jobs in the same session differ by what ran between them, so the
	/// average of those differences is what the next job is expected to add.
	/// </summary>
	/// <param name="snapshotsNewestFirst">The limit windows each recent job reported, newest first.</param>
	public static int? EstimateJobUsagePercent(IEnumerable<IReadOnlyCollection<UsageLimitWindow>> snapshotsNewestFirst)
	{
		var sessions = snapshotsNewestFirst
			.Select(GetSessionWindow)
			.Where(window => window != null)
			.Select(window => window!)
			.ToList();

		var increases = new List<int>();
		for (var index = 0; index + 1 < sessions.Count && increases.Count < EstimateSampleSize; index++)
		{
			var newer = sessions[index];
			var older = sessions[index + 1];
			if ((newer.ResetTime!.Value - older.ResetTime!.Value).Duration() > SameSessionTolerance)
			{
				continue;
			}

			var increase = newer.PercentUsed!.Value - older.PercentUsed!.Value;
			if (increase >= 0)
			{
				increases.Add(increase);
			}
		}

		return increases.Count == 0
			? null
			: (int)Math.Ceiling(increases.Average());
	}

	/// <summary>
	/// Whether these windows include a session, which marks the provider as session-limited
	/// whatever its type.
	/// </summary>
	public static bool HasSessionWindow(IEnumerable<UsageLimitWindow>? windows)
		=> (windows ?? []).Any(window => window?.Scope == UsageLimitWindowScope.Session);

	/// <summary>
	/// Whether these windows include any the policy guards: a session or a longer limit.
	/// </summary>
	public static bool HasGuardedWindow(IEnumerable<UsageLimitWindow>? windows)
		=> (windows ?? []).Any(window => window != null && IsGuardedScope(window.Scope));

	private static UsageLimitWindow? GetSessionWindow(IReadOnlyCollection<UsageLimitWindow> windows)
		=> windows.FirstOrDefault(window => window?.Scope == UsageLimitWindowScope.Session
			&& window.PercentUsed.HasValue
			&& window.ResetTime.HasValue);
}

/// <summary>
/// Jobs on a provider wait until <see cref="Until"/>, when the window in <see cref="Scope"/> resets.
/// </summary>
public sealed record SessionLimitHold(
	DateTime Until,
	int PercentUsed,
	int PauseThresholdPercent,
	int? ExpectedJobUsagePercent,
	UsageLimitWindowScope Scope = UsageLimitWindowScope.Session)
{
	public bool NextJobWouldReachLimit => PercentUsed < PauseThresholdPercent;

	public string Describe(string providerName)
	{
		var window = Scope switch
		{
			UsageLimitWindowScope.Daily => "daily limit",
			UsageLimitWindowScope.Weekly => "weekly limit",
			UsageLimitWindowScope.Monthly => "monthly limit",
			_ => "session"
		};
		var reason = NextJobWouldReachLimit
			? $"{providerName}'s {window} is {PercentUsed}% used and a job typically uses about {ExpectedJobUsagePercent}%"
			: $"{providerName}'s {window} is {PercentUsed}% used (pause threshold {PauseThresholdPercent}%)";

		return $"{reason}. Waiting for the {window} to reset at {Until:u}.";
	}
}

/// <summary>
/// When jobs on a provider stop to protect its usage. A null threshold uses
/// <see cref="SessionLimitPolicy.DefaultPauseThresholdPercent"/>; a null or zero
/// <see cref="ConfiguredUsageLimit"/> sets no monthly request limit.
/// </summary>
public sealed record ProviderUsageLimits(int? SessionPausePercent, int? WeeklyPausePercent, int? ConfiguredUsageLimit);
