using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Service for tracking and managing provider usage, limits, and exhaustion detection.
/// </summary>
public interface IProviderUsageService
{
	/// <summary>
	/// Records usage from a job execution.
	/// Creates a ProviderUsageRecord and updates the ProviderUsageSummary.
	/// </summary>
	/// <param name="providerId">The provider that was used</param>
	/// <param name="jobId">The job that generated the usage (optional)</param>
	/// <param name="executionResult">The execution result containing usage data</param>
	Task RecordUsageAsync(
		Guid providerId,
		Guid? jobId,
		ExecutionResult executionResult,
		CancellationToken cancellationToken = default);

	/// <returns>The usage summary, or null if not found</returns>
	Task<ProviderUsageSummary?> GetUsageSummaryAsync(
		Guid providerId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets usage summaries for all providers (for dashboard).
	/// </summary>
	/// <returns>Dictionary mapping provider ID to usage summary</returns>
	Task<Dictionary<Guid, ProviderUsageSummary>> GetAllUsageSummariesAsync(
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets usage history records for a provider.
	/// </summary>
	/// <param name="limit">Maximum number of records to return</param>
	/// <returns>List of usage records, most recent first</returns>
	Task<List<ProviderUsageRecord>> GetUsageHistoryAsync(
		Guid providerId,
		int limit = 100,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates the cached version information for a provider.
	/// </summary>
	/// <param name="version">The CLI version string</param>
	Task UpdateVersionInfoAsync(
		Guid providerId,
		string version,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks if a provider is approaching or has reached its usage limit.
	/// </summary>
	/// <param name="warningThreshold">Percentage threshold for warning (default 80%)</param>
	/// <returns>Warning information if approaching limit, null otherwise</returns>
	Task<UsageExhaustionWarning?> CheckExhaustionAsync(
		Guid providerId,
		int warningThreshold = 80,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Stores usage limits observed outside a job execution, such as an on-demand refresh.
	/// Updates only the limit state; cumulative job totals are left alone.
	/// </summary>
	/// <param name="providerId">The provider the limits belong to</param>
	/// <param name="limits">The limits reported by the provider</param>
	Task<ProviderUsageSummary> ApplyDetectedLimitsAsync(
		Guid providerId,
		UsageLimits limits,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Resets the usage period for a provider (e.g., on monthly reset).
	/// Archives current totals and starts a new period.
	/// </summary>
	Task ResetPeriodAsync(
		Guid providerId,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Warning information when a provider is approaching or has exceeded its usage limit.
/// </summary>
public class UsageExhaustionWarning
{
	public Guid ProviderId { get; set; }
	public string ProviderName { get; set; } = string.Empty;

	/// <summary>
	/// Current percentage of limit used (0-100+)
	/// </summary>
	public int PercentUsed { get; set; }

	public string Message { get; set; } = string.Empty;
	public DateTime? ResetTime { get; set; }
	public bool IsExhausted { get; set; }
	public bool ShouldPauseProcessing { get; set; }
	public UsageLimitType LimitType { get; set; }
	public int? CurrentUsage { get; set; }
	public int? MaxUsage { get; set; }
}
