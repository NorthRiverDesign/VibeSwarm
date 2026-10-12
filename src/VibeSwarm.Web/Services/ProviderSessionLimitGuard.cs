using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Reads a provider's stored session usage and decides whether jobs on it should wait for the
/// session to reset. See <see cref="SessionLimitPolicy"/>.
/// </summary>
public static class ProviderSessionLimitGuard
{
	/// <summary>
	/// How many recent jobs the per-job usage estimate draws on.
	/// </summary>
	private const int RecentJobRecordCount = 20;

	/// <summary>
	/// A live reading this recent is reused rather than taken again, so jobs starting together
	/// share one check.
	/// </summary>
	public static readonly TimeSpan LiveReadingFreshness = TimeSpan.FromMinutes(2);

	public static async Task<SessionLimitHold?> GetHoldAsync(
		VibeSwarmDbContext dbContext,
		Guid providerId,
		DateTime utcNow,
		CancellationToken cancellationToken)
	{
		var summary = await dbContext.ProviderUsageSummaries
			.AsNoTracking()
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);

		if (summary == null
			|| ProviderMetering.IsUnmetered(summary.LimitType)
			|| !SessionLimitPolicy.HasGuardedWindow(summary.LimitWindows))
		{
			return null;
		}

		var configuredThresholds = await dbContext.Providers
			.AsNoTracking()
			.Where(p => p.Id == providerId)
			.Select(p => new { Session = p.SessionLimitPauseThresholdPercent, Weekly = p.WeeklyLimitPauseThresholdPercent })
			.FirstOrDefaultAsync(cancellationToken);

		var recentJobRecords = await dbContext.ProviderUsageRecords
			.AsNoTracking()
			.Where(r => r.ProviderId == providerId && r.JobId != null && r.DetectedLimitWindowsJson != null)
			.OrderByDescending(r => r.RecordedAt)
			.Take(RecentJobRecordCount)
			.ToListAsync(cancellationToken);

		var expectedJobUsage = SessionLimitPolicy.EstimateJobUsagePercent(
			recentJobRecords.Select(r => (IReadOnlyCollection<UsageLimitWindow>)r.DetectedLimitWindows));

		return SessionLimitPolicy.Evaluate(
			summary.LimitWindows,
			SessionLimitPolicy.ResolvePauseThreshold(configuredThresholds?.Session),
			expectedJobUsage,
			utcNow,
			SessionLimitPolicy.ResolvePauseThreshold(configuredThresholds?.Weekly));
	}

	/// <summary>
	/// Whether the provider's usage should be read live before a job starts: it has reported a
	/// session or a longer limit before, no recent live reading exists, and the stored figures
	/// don't already hold jobs back (usage only grows until a window resets). A provider whose
	/// reading is free (<paramref name="readingIsFree"/>, such as Copilot's account quota) is read
	/// even before it has reported anything, so its first job is already guarded.
	/// </summary>
	public static bool NeedsLiveReading(ProviderUsageSummary? summary, SessionLimitHold? storedHold, DateTime utcNow, bool readingIsFree = false)
	{
		if (readingIsFree)
		{
			return storedHold == null
				&& (summary?.LimitsRefreshedAt is not DateTime lastReading || utcNow - lastReading > LiveReadingFreshness);
		}

		return summary != null
			&& storedHold == null
			&& !ProviderMetering.IsUnmetered(summary.LimitType)
			&& SessionLimitPolicy.HasGuardedWindow(summary.LimitWindows)
			&& (summary.LimitsRefreshedAt is not DateTime refreshedAt || utcNow - refreshedAt > LiveReadingFreshness);
	}
}
