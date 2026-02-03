using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Append-only record of provider usage from a single job execution.
/// Used for historical tracking, auditing, and cost analysis.
/// </summary>
public class ProviderUsageRecord
{
	public Guid Id { get; set; } = Guid.NewGuid();

	/// <summary>
	/// The provider that was used
	/// </summary>
	public Guid ProviderId { get; set; }

	/// <summary>
	/// Navigation property to the provider
	/// </summary>
	public Provider? Provider { get; set; }

	/// <summary>
	/// The job that generated this usage (optional - could be from a test or refresh)
	/// </summary>
	public Guid? JobId { get; set; }

	/// <summary>
	/// Navigation property to the job
	/// </summary>
	public Job? Job { get; set; }

	/// <summary>
	/// Number of input tokens consumed
	/// </summary>
	public int? InputTokens { get; set; }

	/// <summary>
	/// Number of output tokens generated
	/// </summary>
	public int? OutputTokens { get; set; }

	/// <summary>
	/// Estimated cost in USD for this execution
	/// </summary>
	public decimal? CostUsd { get; set; }

	/// <summary>
	/// Number of premium requests consumed (Copilot-specific)
	/// </summary>
	public int? PremiumRequestsConsumed { get; set; }

	/// <summary>
	/// The AI model that was used (e.g., "claude-sonnet-4-20250514")
	/// </summary>
	[StringLength(200)]
	public string? ModelUsed { get; set; }

	/// <summary>
	/// Type of limit detected from CLI output
	/// </summary>
	public UsageLimitType? DetectedLimitType { get; set; }

	/// <summary>
	/// Current usage count as reported by the provider
	/// </summary>
	public int? DetectedCurrentUsage { get; set; }

	/// <summary>
	/// Maximum allowed usage as reported by the provider
	/// </summary>
	public int? DetectedMaxUsage { get; set; }

	/// <summary>
	/// When the limit resets (if reported by the provider)
	/// </summary>
	public DateTime? DetectedResetTime { get; set; }

	/// <summary>
	/// Whether the limit was reached during this execution
	/// </summary>
	public bool DetectedLimitReached { get; set; }

	/// <summary>
	/// Raw message from CLI output about limits (for debugging/analysis)
	/// </summary>
	[StringLength(1000)]
	public string? RawLimitMessage { get; set; }

	/// <summary>
	/// When this usage record was created
	/// </summary>
	public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
