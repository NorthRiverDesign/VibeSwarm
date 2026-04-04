namespace VibeSwarm.Shared.Models;

public sealed class ProjectJobsListResult : PagedResult<JobSummary>
{
	public int ActiveCount { get; set; }
	public int CompletedCount { get; set; }
	public int TotalInputTokens { get; set; }
	public int TotalOutputTokens { get; set; }
	public decimal TotalCostUsd { get; set; }
	public JobSummary? ActiveJobSummary { get; set; }
}
