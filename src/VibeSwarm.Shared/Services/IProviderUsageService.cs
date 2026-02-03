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
	/// <param name="cancellationToken">Cancellation token</param>
	Task RecordUsageAsync(
		Guid providerId,
		Guid? jobId,
		ExecutionResult executionResult,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the usage summary for a single provider.
	/// </summary>
	/// <param name="providerId">The provider ID</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The usage summary, or null if not found</returns>
	Task<ProviderUsageSummary?> GetUsageSummaryAsync(
		Guid providerId,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets usage summaries for all providers (for dashboard).
	/// </summary>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>Dictionary mapping provider ID to usage summary</returns>
	Task<Dictionary<Guid, ProviderUsageSummary>> GetAllUsageSummariesAsync(
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets usage history records for a provider.
	/// </summary>
	/// <param name="providerId">The provider ID</param>
	/// <param name="limit">Maximum number of records to return</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>List of usage records, most recent first</returns>
	Task<List<ProviderUsageRecord>> GetUsageHistoryAsync(
		Guid providerId,
		int limit = 100,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Updates the cached version information for a provider.
	/// </summary>
	/// <param name="providerId">The provider ID</param>
	/// <param name="version">The CLI version string</param>
	/// <param name="cancellationToken">Cancellation token</param>
	Task UpdateVersionInfoAsync(
		Guid providerId,
		string version,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks if a provider is approaching or has reached its usage limit.
	/// </summary>
	/// <param name="providerId">The provider ID</param>
	/// <param name="warningThreshold">Percentage threshold for warning (default 80%)</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>Warning information if approaching limit, null otherwise</returns>
	Task<UsageExhaustionWarning?> CheckExhaustionAsync(
		Guid providerId,
		int warningThreshold = 80,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Resets the usage period for a provider (e.g., on monthly reset).
	/// Archives current totals and starts a new period.
	/// </summary>
	/// <param name="providerId">The provider ID</param>
	/// <param name="cancellationToken">Cancellation token</param>
	Task ResetPeriodAsync(
		Guid providerId,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Warning information when a provider is approaching or has exceeded its usage limit.
/// </summary>
public class UsageExhaustionWarning
{
	/// <summary>
	/// The provider ID this warning is for
	/// </summary>
	public Guid ProviderId { get; set; }

	/// <summary>
	/// The provider name for display
	/// </summary>
	public string ProviderName { get; set; } = string.Empty;

	/// <summary>
	/// Current percentage of limit used (0-100+)
	/// </summary>
	public int PercentUsed { get; set; }

	/// <summary>
	/// Human-readable warning message
	/// </summary>
	public string Message { get; set; } = string.Empty;

	/// <summary>
	/// When the limit resets (if known)
	/// </summary>
	public DateTime? ResetTime { get; set; }

	/// <summary>
	/// True if the limit has been reached or exceeded
	/// </summary>
	public bool IsExhausted { get; set; }

	/// <summary>
	/// True if processing should be paused for this provider
	/// </summary>
	public bool ShouldPauseProcessing { get; set; }

	/// <summary>
	/// Type of limit that is approaching/exceeded
	/// </summary>
	public UsageLimitType LimitType { get; set; }

	/// <summary>
	/// Current usage count
	/// </summary>
	public int? CurrentUsage { get; set; }

	/// <summary>
	/// Maximum allowed usage
	/// </summary>
	public int? MaxUsage { get; set; }
}
