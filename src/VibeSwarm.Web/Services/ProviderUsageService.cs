using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Implementation of provider usage tracking service.
/// Records usage from job executions, maintains summaries, and detects exhaustion.
/// </summary>
public class ProviderUsageService : IProviderUsageService
{
	private readonly VibeSwarmDbContext _context;
	private readonly ILogger<ProviderUsageService> _logger;

	public ProviderUsageService(VibeSwarmDbContext context, ILogger<ProviderUsageService> logger)
	{
		_context = context;
		_logger = logger;
	}

	public async Task RecordUsageAsync(
		Guid providerId,
		Guid? jobId,
		ExecutionResult executionResult,
		CancellationToken cancellationToken = default)
	{
		var provider = await _context.Providers
			.AsNoTracking()
			.FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken);

		// Create the usage record
		var record = new ProviderUsageRecord
		{
			ProviderId = providerId,
			JobId = jobId,
			InputTokens = executionResult.InputTokens,
			OutputTokens = executionResult.OutputTokens,
			CostUsd = executionResult.CostUsd,
			PremiumRequestsConsumed = executionResult.PremiumRequestsConsumed,
			ModelUsed = executionResult.ModelUsed,
			RecordedAt = DateTime.UtcNow
		};

		// If detected usage limits exist, record them
		if (executionResult.DetectedUsageLimits != null)
		{
			var limits = executionResult.DetectedUsageLimits;
			var detectedWindows = BuildWindowsFromSnapshot(limits);
			record.DetectedLimitType = limits.LimitType;
			record.DetectedCurrentUsage = limits.CurrentUsage;
			record.DetectedMaxUsage = limits.MaxUsage;
			record.DetectedResetTime = limits.ResetTime;
			record.DetectedLimitReached = limits.IsLimitReached;
			record.RawLimitMessage = limits.Message;
			record.DetectedLimitWindows = detectedWindows;
		}

		_context.ProviderUsageRecords.Add(record);

		// Update or create the summary
		var summary = await _context.ProviderUsageSummaries
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);

		if (summary == null)
		{
			summary = new ProviderUsageSummary
			{
				ProviderId = providerId,
				PeriodStart = DateTime.UtcNow
			};
			_context.ProviderUsageSummaries.Add(summary);
		}

		summary.ConfiguredMaxUsage = provider?.ConfiguredUsageLimit;

		// Update cumulative totals
		summary.TotalInputTokens += executionResult.InputTokens ?? 0;
		summary.TotalOutputTokens += executionResult.OutputTokens ?? 0;
		summary.TotalCostUsd += executionResult.CostUsd ?? 0;
		summary.TotalPremiumRequestsConsumed += executionResult.PremiumRequestsConsumed ?? 0;

		if (jobId.HasValue)
		{
			summary.TotalJobsCompleted++;
		}

		var configuredLimitType = provider?.ConfiguredLimitType ?? UsageLimitType.None;
		if (summary.LimitType == UsageLimitType.None && configuredLimitType != UsageLimitType.None)
		{
			summary.LimitType = configuredLimitType;
		}

		// Update limit state from detected limits
		if (executionResult.DetectedUsageLimits != null)
		{
			var limits = executionResult.DetectedUsageLimits;
			summary.LimitWindows = BuildWindowsFromSnapshot(limits);
			ApplyLimitSnapshot(summary, limits);
		}

		await ApplyTrackedUsageWindowsAsync(summary, provider, executionResult, cancellationToken);

		summary.LastUpdatedAt = DateTime.UtcNow;

		await _context.SaveChangesAsync(cancellationToken);

		_logger.LogDebug(
			"Recorded usage for provider {ProviderId}: {InputTokens} in, {OutputTokens} out, {PremiumRequests} premium requests",
			providerId,
			executionResult.InputTokens,
			executionResult.OutputTokens,
			executionResult.PremiumRequestsConsumed);
	}

	public async Task<ProviderUsageSummary?> GetUsageSummaryAsync(
		Guid providerId,
		CancellationToken cancellationToken = default)
	{
		return await _context.ProviderUsageSummaries
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);
	}

	public async Task<Dictionary<Guid, ProviderUsageSummary>> GetAllUsageSummariesAsync(
		CancellationToken cancellationToken = default)
	{
		return await _context.ProviderUsageSummaries
			.ToDictionaryAsync(s => s.ProviderId, cancellationToken);
	}

	public async Task<List<ProviderUsageRecord>> GetUsageHistoryAsync(
		Guid providerId,
		int limit = 100,
		CancellationToken cancellationToken = default)
	{
		return await _context.ProviderUsageRecords
			.Where(r => r.ProviderId == providerId)
			.OrderByDescending(r => r.RecordedAt)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task UpdateVersionInfoAsync(
		Guid providerId,
		string version,
		CancellationToken cancellationToken = default)
	{
		var summary = await _context.ProviderUsageSummaries
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);

		if (summary == null)
		{
			summary = new ProviderUsageSummary
			{
				ProviderId = providerId,
				PeriodStart = DateTime.UtcNow
			};
			_context.ProviderUsageSummaries.Add(summary);
		}

		summary.CliVersion = version;
		summary.VersionCheckedAt = DateTime.UtcNow;
		summary.LastUpdatedAt = DateTime.UtcNow;

		await _context.SaveChangesAsync(cancellationToken);

		_logger.LogDebug("Updated CLI version for provider {ProviderId}: {Version}", providerId, version);
	}

	public async Task<UsageExhaustionWarning?> CheckExhaustionAsync(
		Guid providerId,
		int warningThreshold = 80,
		CancellationToken cancellationToken = default)
	{
		var summary = await _context.ProviderUsageSummaries
			.Include(s => s.Provider)
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);

		if (summary == null)
			return null;

		// Get the effective max usage (user-configured or detected)
		var effectiveMax = summary.EffectiveMaxUsage;
		if (!effectiveMax.HasValue || effectiveMax <= 0)
			return null; // No limit configured, can't check exhaustion

		var currentUsage = summary.CurrentUsage ?? 0;
		var percentUsed = (int)((currentUsage / (double)effectiveMax.Value) * 100);

		// Only return a warning if we're at or above the threshold
		if (percentUsed < warningThreshold && !summary.IsLimitReached)
			return null;

		var isExhausted = percentUsed >= 100 || summary.IsLimitReached;
		var providerName = summary.Provider?.Name ?? "Unknown Provider";

		var message = isExhausted
			? $"{providerName} has reached its usage limit ({currentUsage}/{effectiveMax})"
			: $"{providerName} is at {percentUsed}% of its usage limit ({currentUsage}/{effectiveMax})";

		if (summary.LimitResetTime.HasValue)
		{
			message += $". Resets at {summary.LimitResetTime.Value:g}";
		}

		return new UsageExhaustionWarning
		{
			ProviderId = providerId,
			ProviderName = providerName,
			PercentUsed = percentUsed,
			Message = message,
			ResetTime = summary.LimitResetTime,
			IsExhausted = isExhausted,
			ShouldPauseProcessing = isExhausted,
			LimitType = summary.LimitType,
			CurrentUsage = currentUsage,
			MaxUsage = effectiveMax
		};
	}

	public async Task ResetPeriodAsync(
		Guid providerId,
		CancellationToken cancellationToken = default)
	{
		var summary = await _context.ProviderUsageSummaries
			.FirstOrDefaultAsync(s => s.ProviderId == providerId, cancellationToken);

		if (summary == null)
			return;

		// Reset cumulative totals for the new period
		summary.TotalInputTokens = 0;
		summary.TotalOutputTokens = 0;
		summary.TotalCostUsd = 0;
		summary.TotalJobsCompleted = 0;
		summary.TotalPremiumRequestsConsumed = 0;

		// Reset limit state
		summary.CurrentUsage = 0;
		summary.IsLimitReached = false;
		summary.LimitMessage = null;
		summary.LimitResetTime = null;
		summary.LimitWindows = [];

		// Update period tracking
		summary.PeriodStart = DateTime.UtcNow;
		summary.LastUpdatedAt = DateTime.UtcNow;

		await _context.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Reset usage period for provider {ProviderId}", providerId);
	}

	private void ApplyLimitSnapshot(ProviderUsageSummary summary, UsageLimits limits)
	{
		var primaryWindow = UsageLimitWindowHelper.SelectPrimaryWindow(limits.Windows);
		summary.LimitType = primaryWindow?.LimitType ?? limits.LimitType;
		summary.CurrentUsage = primaryWindow?.CurrentUsage ?? limits.CurrentUsage;
		summary.MaxUsage = primaryWindow?.MaxUsage ?? limits.MaxUsage;
		summary.LimitResetTime = primaryWindow?.ResetTime ?? limits.ResetTime;
		summary.IsLimitReached = limits.IsLimitReached || (primaryWindow?.IsLimitReached ?? false);
		summary.LimitMessage = primaryWindow?.Message ?? limits.Message;
	}

	private async Task ApplyTrackedUsageWindowsAsync(
		ProviderUsageSummary summary,
		Provider? provider,
		ExecutionResult executionResult,
		CancellationToken cancellationToken)
	{
		var currentWindows = UsageLimitWindowHelper.NormalizeWindows(summary.LimitWindows);
		var hasProviderCurrentUsage = currentWindows.Any(window => window.CurrentUsage.HasValue && !IsTrackedMonthlyPremiumWindow(window));

		if ((summary.LimitType == UsageLimitType.PremiumRequests
				|| provider?.ConfiguredLimitType == UsageLimitType.PremiumRequests)
			&& !hasProviderCurrentUsage)
		{
			var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
			var nextMonth = monthStart.AddMonths(1);
			var recordedThisMonth = await _context.ProviderUsageRecords
				.Where(record => record.ProviderId == summary.ProviderId && record.RecordedAt >= monthStart)
				.SumAsync(record => record.PremiumRequestsConsumed ?? 0, cancellationToken);

			recordedThisMonth += executionResult.PremiumRequestsConsumed ?? 0;

			var trackedWindow = new UsageLimitWindow
			{
				Scope = UsageLimitWindowScope.Monthly,
				LimitType = UsageLimitType.PremiumRequests,
				CurrentUsage = recordedThisMonth,
				MaxUsage = provider?.ConfiguredUsageLimit,
				ResetTime = nextMonth,
				IsLimitReached = provider?.ConfiguredUsageLimit is int configuredMax && configuredMax > 0 && recordedThisMonth >= configuredMax,
				Message = provider?.ConfiguredUsageLimit is int configuredLimit && configuredLimit > 0
					? $"Tracked monthly premium requests in VibeSwarm: {recordedThisMonth}/{configuredLimit}"
					: $"Tracked monthly premium requests in VibeSwarm: {recordedThisMonth}"
			};

			currentWindows.RemoveAll(IsTrackedMonthlyPremiumWindow);
			currentWindows.Add(trackedWindow);
			summary.LimitWindows = UsageLimitWindowHelper.NormalizeWindows(currentWindows);
			ApplyLimitSnapshot(summary, UsageLimitWindowHelper.CreateUsageLimits(
				summary.LimitType == UsageLimitType.None ? UsageLimitType.PremiumRequests : summary.LimitType,
				trackedWindow.Message,
				summary.LimitWindows,
				trackedWindow.IsLimitReached));
			summary.PeriodStart = monthStart;
			return;
		}

		summary.LimitWindows = currentWindows;
		if (currentWindows.Count > 0)
		{
			ApplyLimitSnapshot(summary, UsageLimitWindowHelper.CreateUsageLimits(summary.LimitType, summary.LimitMessage, currentWindows, summary.IsLimitReached));
		}
	}

	private static List<UsageLimitWindow> BuildWindowsFromSnapshot(UsageLimits limits)
	{
		var windows = UsageLimitWindowHelper.NormalizeWindows(limits.Windows);
		if (windows.Count > 0)
		{
			return windows;
		}

		if (!limits.CurrentUsage.HasValue
			&& !limits.MaxUsage.HasValue
			&& !limits.ResetTime.HasValue
			&& string.IsNullOrWhiteSpace(limits.Message)
			&& !limits.IsLimitReached)
		{
			return [];
		}

		return
		[
			new UsageLimitWindow
			{
				Scope = InferScope(limits.LimitType),
				LimitType = limits.LimitType,
				CurrentUsage = limits.CurrentUsage,
				MaxUsage = limits.MaxUsage,
				ResetTime = limits.ResetTime,
				IsLimitReached = limits.IsLimitReached,
				Message = limits.Message
			}
		];
	}

	private static bool IsTrackedMonthlyPremiumWindow(UsageLimitWindow window)
	{
		return window.Scope == UsageLimitWindowScope.Monthly
			&& window.LimitType == UsageLimitType.PremiumRequests
			&& window.Message?.StartsWith("Tracked monthly premium requests in VibeSwarm:", StringComparison.Ordinal) == true;
	}

	private static UsageLimitWindowScope InferScope(UsageLimitType limitType)
	{
		return limitType switch
		{
			UsageLimitType.SessionLimit => UsageLimitWindowScope.Session,
			UsageLimitType.PremiumRequests => UsageLimitWindowScope.Monthly,
			_ => UsageLimitWindowScope.Unknown
		};
	}
}
