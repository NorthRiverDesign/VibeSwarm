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
			record.DetectedLimitType = limits.LimitType;
			record.DetectedCurrentUsage = limits.CurrentUsage;
			record.DetectedMaxUsage = limits.MaxUsage;
			record.DetectedResetTime = limits.ResetTime;
			record.DetectedLimitReached = limits.IsLimitReached;
			record.RawLimitMessage = limits.Message;
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

		// Update cumulative totals
		summary.TotalInputTokens += executionResult.InputTokens ?? 0;
		summary.TotalOutputTokens += executionResult.OutputTokens ?? 0;
		summary.TotalCostUsd += executionResult.CostUsd ?? 0;
		summary.TotalPremiumRequestsConsumed += executionResult.PremiumRequestsConsumed ?? 0;

		if (jobId.HasValue)
		{
			summary.TotalJobsCompleted++;
		}

		// Update limit state from detected limits
		if (executionResult.DetectedUsageLimits != null)
		{
			var limits = executionResult.DetectedUsageLimits;
			summary.LimitType = limits.LimitType;
			summary.CurrentUsage = limits.CurrentUsage;
			summary.MaxUsage = limits.MaxUsage;
			summary.LimitResetTime = limits.ResetTime;
			summary.IsLimitReached = limits.IsLimitReached;
			summary.LimitMessage = limits.Message;
		}

		// For Copilot, track premium requests as current usage if no other limit detected
		if (executionResult.PremiumRequestsConsumed.HasValue && summary.LimitType == UsageLimitType.PremiumRequests)
		{
			summary.CurrentUsage = (summary.CurrentUsage ?? 0) + executionResult.PremiumRequestsConsumed.Value;
		}

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

		// Update period tracking
		summary.PeriodStart = DateTime.UtcNow;
		summary.LastUpdatedAt = DateTime.UtcNow;

		await _context.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Reset usage period for provider {ProviderId}", providerId);
	}
}
