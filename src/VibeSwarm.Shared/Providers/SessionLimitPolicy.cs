namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Decides when a provider's session, its short rolling usage window such as Claude's five
/// hours, is too close to its limit to start another job.
/// </summary>
/// <remarks>
/// A job that runs into the limit midway stops with its work half done. Holding new jobs back
/// a little early and resuming once the session resets costs only waiting time.
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

	public static int ResolvePauseThreshold(int? configuredPercent)
		=> configuredPercent is int percent
			? Math.Clamp(percent, MinPauseThresholdPercent, MaxPauseThresholdPercent)
			: DefaultPauseThresholdPercent;

	/// <summary>
	/// Returns the hold that should keep jobs from starting, or null when the session has room.
	/// A session holds jobs once its usage reaches <paramref name="pauseThresholdPercent"/>, or
	/// earlier (from half used) when one more typical job would use up what is left.
	/// </summary>
	public static SessionLimitHold? Evaluate(
		IEnumerable<UsageLimitWindow>? windows,
		int pauseThresholdPercent,
		int? expectedJobUsagePercent,
		DateTime utcNow)
	{
		SessionLimitHold? hold = null;
		foreach (var window in windows ?? [])
		{
			// A window whose reset time has passed has already started over, and one without a
			// reset time gives no point to wait for.
			if (window?.Scope != UsageLimitWindowScope.Session
				|| window.ResetTime is not DateTime resetTime
				|| resetTime <= utcNow)
			{
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

	private static UsageLimitWindow? GetSessionWindow(IReadOnlyCollection<UsageLimitWindow> windows)
		=> windows.FirstOrDefault(window => window?.Scope == UsageLimitWindowScope.Session
			&& window.PercentUsed.HasValue
			&& window.ResetTime.HasValue);
}

/// <summary>
/// Jobs on a provider wait until <see cref="Until"/>, when its session resets.
/// </summary>
public sealed record SessionLimitHold(
	DateTime Until,
	int PercentUsed,
	int PauseThresholdPercent,
	int? ExpectedJobUsagePercent)
{
	public bool NextJobWouldReachLimit => PercentUsed < PauseThresholdPercent;

	public string Describe(string providerName)
	{
		var reason = NextJobWouldReachLimit
			? $"{providerName}'s session is {PercentUsed}% used and a job typically uses about {ExpectedJobUsagePercent}%"
			: $"{providerName}'s session is {PercentUsed}% used (pause threshold {PauseThresholdPercent}%)";

		return $"{reason}. Waiting for the session to reset at {Until:u}.";
	}
}
