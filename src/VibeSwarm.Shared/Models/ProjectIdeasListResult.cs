using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

public sealed class ProjectIdeasListResult : PagedResult<Idea>
{
	public int UnprocessedCount { get; set; }
}
