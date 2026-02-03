using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Rolling summary of provider usage. One row per provider for fast dashboard reads.
/// Updated after each job execution or usage detection event.
/// </summary>
public class ProviderUsageSummary
{
	public Guid Id { get; set; } = Guid.NewGuid();

	/// <summary>
	/// The provider this summary is for. One summary per provider.
	/// </summary>
	public Guid ProviderId { get; set; }

	/// <summary>
	/// Navigation property to the provider
	/// </summary>
	public Provider? Provider { get; set; }

	#region Cumulative Totals

	/// <summary>
	/// Total input tokens consumed across all jobs for this provider
	/// </summary>
	public long TotalInputTokens { get; set; }

	/// <summary>
	/// Total output tokens generated across all jobs for this provider
	/// </summary>
	public long TotalOutputTokens { get; set; }

	/// <summary>
	/// Total estimated cost in USD across all jobs for this provider
	/// </summary>
	public decimal TotalCostUsd { get; set; }

	/// <summary>
	/// Total number of jobs completed using this provider
	/// </summary>
	public int TotalJobsCompleted { get; set; }

	/// <summary>
	/// Total premium requests consumed (Copilot-specific)
	/// </summary>
	public int TotalPremiumRequestsConsumed { get; set; }

	#endregion

	#region Latest Limit State

	/// <summary>
	/// Type of limit currently tracked for this provider
	/// </summary>
	public UsageLimitType LimitType { get; set; } = UsageLimitType.None;

	/// <summary>
	/// Current usage count toward the limit
	/// </summary>
	public int? CurrentUsage { get; set; }

	/// <summary>
	/// Maximum allowed usage (as detected from provider output)
	/// </summary>
	public int? MaxUsage { get; set; }

	/// <summary>
	/// When the current limit period resets
	/// </summary>
	public DateTime? LimitResetTime { get; set; }

	/// <summary>
	/// Whether the limit has been reached
	/// </summary>
	public bool IsLimitReached { get; set; }

	/// <summary>
	/// Human-readable message about the limit status
	/// </summary>
	[StringLength(500)]
	public string? LimitMessage { get; set; }

	#endregion

	#region User-Configurable Limit

	/// <summary>
	/// User-configured maximum usage limit (e.g., Copilot Pro = 300 premium requests/month).
	/// Takes precedence over detected MaxUsage for exhaustion calculations.
	/// </summary>
	public int? ConfiguredMaxUsage { get; set; }

	#endregion

	#region Cached Version Info

	/// <summary>
	/// Cached CLI version string (e.g., "v1.0.8")
	/// </summary>
	[StringLength(50)]
	public string? CliVersion { get; set; }

	/// <summary>
	/// When the CLI version was last checked
	/// </summary>
	public DateTime? VersionCheckedAt { get; set; }

	#endregion

	#region Period Tracking

	/// <summary>
	/// Start of the current tracking period (for monthly/weekly resets)
	/// </summary>
	public DateTime PeriodStart { get; set; } = DateTime.UtcNow;

	/// <summary>
	/// When this summary was last updated
	/// </summary>
	public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

	#endregion

	#region Computed Properties

	/// <summary>
	/// Gets the effective maximum usage for exhaustion calculations.
	/// Returns user-configured limit if set, otherwise detected max.
	/// </summary>
	public int? EffectiveMaxUsage => ConfiguredMaxUsage ?? MaxUsage;

	/// <summary>
	/// Gets the percentage of limit used (0-100, null if unknown).
	/// Uses CurrentUsage against EffectiveMaxUsage.
	/// </summary>
	public int? PercentUsed
	{
		get
		{
			var max = EffectiveMaxUsage;
			if (CurrentUsage.HasValue && max.HasValue && max > 0)
				return (int)((CurrentUsage.Value / (double)max.Value) * 100);
			return null;
		}
	}

	#endregion
}
