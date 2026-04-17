using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

public class JobStatistics
{
	public Guid JobId { get; set; }

	[JsonIgnore]
	public Job? Job { get; set; }

	public double? ExecutionDurationSeconds { get; set; }

	public decimal? TotalCostUsd { get; set; }

	public int? InputTokens { get; set; }

	public int? OutputTokens { get; set; }

	/// <summary>
	/// True when InputTokens/OutputTokens are estimates derived from text length
	/// rather than exact counts reported by the provider.
	/// </summary>
	public bool IsTokenEstimate { get; set; }
}
