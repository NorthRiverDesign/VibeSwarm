using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Append-only record of provider usage from a single job execution.
/// Used for historical tracking, auditing, and cost analysis.
/// </summary>
public class ProviderUsageRecord
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid ProviderId { get; set; }
	public Provider? Provider { get; set; }

	/// <summary>
	/// The job that generated this usage (optional - could be from a test or refresh)
	/// </summary>
	public Guid? JobId { get; set; }

	public Job? Job { get; set; }
	public int? InputTokens { get; set; }
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
	/// GitHub AI Units consumed by this execution (Copilot-specific).
	/// </summary>
	public decimal? AiCreditsConsumed { get; set; }

	/// <summary>
	/// The AI model that was used (e.g., "claude-sonnet-4-20250514")
	/// </summary>
	[StringLength(200)]
	public string? ModelUsed { get; set; }

	public UsageLimitType? DetectedLimitType { get; set; }
	public int? DetectedCurrentUsage { get; set; }
	public int? DetectedMaxUsage { get; set; }
	public DateTime? DetectedResetTime { get; set; }
	public bool DetectedLimitReached { get; set; }

	/// <summary>
	/// Raw message from CLI output about limits (for debugging/analysis)
	/// </summary>
	[StringLength(1000)]
	public string? RawLimitMessage { get; set; }

	/// <summary>
	/// Persisted JSON payload of detailed limit windows observed for this execution.
	/// </summary>
	[StringLength(4000)]
	[JsonIgnore]
	public string? DetectedLimitWindowsJson
	{
		get => _detectedLimitWindowsJson;
		set
		{
			_detectedLimitWindowsJson = value;
			_detectedLimitWindows = null;
		}
	}

	/// <summary>
	/// Detailed limit windows observed for this execution.
	/// </summary>
	[NotMapped]
	public List<UsageLimitWindow> DetectedLimitWindows
	{
		get
		{
			if (_detectedLimitWindows != null)
			{
				return _detectedLimitWindows;
			}

			_detectedLimitWindows = string.IsNullOrWhiteSpace(_detectedLimitWindowsJson)
				? []
				: JsonSerializer.Deserialize<List<UsageLimitWindow>>(_detectedLimitWindowsJson) ?? [];

			return _detectedLimitWindows;
		}
		set
		{
			_detectedLimitWindows = value ?? [];
			_detectedLimitWindowsJson = _detectedLimitWindows.Count == 0
				? null
				: JsonSerializer.Serialize(_detectedLimitWindows);
		}
	}

	public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
	private string? _detectedLimitWindowsJson;
	private List<UsageLimitWindow>? _detectedLimitWindows;
}
