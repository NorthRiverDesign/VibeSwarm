using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

public class JobExecutionStatistics
{
	public Guid JobId { get; set; }

	[JsonIgnore]
	public Job? Job { get; set; }

	public int? InputTokens { get; set; }

	public int? OutputTokens { get; set; }

	public decimal? CostUsd { get; set; }
}
