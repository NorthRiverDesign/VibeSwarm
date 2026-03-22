namespace VibeSwarm.Shared.Models;

public sealed class ProjectJobsListResult : PagedResult<JobSummary>
{
	public int ActiveCount { get; set; }
	public int CompletedCount { get; set; }
}
