namespace VibeSwarm.Shared.Models;

public sealed class JobsListResult : PagedResult<JobSummary>
{
	public int TotalInputTokens { get; set; }

	public int TotalOutputTokens { get; set; }

	public decimal TotalCostUsd { get; set; }

	public List<JobProjectCountSummary> ProjectCounts { get; set; } = new();
}
