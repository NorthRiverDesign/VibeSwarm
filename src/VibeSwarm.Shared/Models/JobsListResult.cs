using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

public sealed class JobsListResult : PagedResult<Job>
{
	public int TotalInputTokens { get; set; }

	public int TotalOutputTokens { get; set; }

	public decimal TotalCostUsd { get; set; }

	public List<JobProjectCountSummary> ProjectCounts { get; set; } = new();
}
