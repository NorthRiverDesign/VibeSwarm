using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

public sealed class ProjectJobsListResult : PagedResult<Job>
{
	public int ActiveCount { get; set; }
}
