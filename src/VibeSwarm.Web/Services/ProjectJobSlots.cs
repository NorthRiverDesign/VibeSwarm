namespace VibeSwarm.Web.Services;

/// <summary>
/// Which queued jobs a project can start. A project runs one job at a time, because every job
/// resets and cleans the project's checkout before it starts. The exception is a team swarm:
/// its members each run in a git worktree of their own, so members of one swarm run side by
/// side, while anything else in the project waits for the whole swarm to finish.
/// </summary>
internal sealed class ProjectJobSlots
{
	private readonly Dictionary<Guid, HashSet<Guid?>> _inFlight = new();

	public ProjectJobSlots(IEnumerable<(Guid ProjectId, Guid? SwarmId)> runningJobs)
	{
		foreach (var (projectId, swarmId) in runningJobs)
		{
			Take(projectId, swarmId);
		}
	}

	/// <summary>
	/// Projects running a job that isn't a swarm member, or members of more than one swarm:
	/// nothing else can start there.
	/// </summary>
	public IReadOnlyList<Guid> ClosedProjectIds => _inFlight
		.Where(entry => entry.Value.Count > 1 || entry.Value.Contains(null))
		.Select(entry => entry.Key)
		.ToList();

	public bool CanStart(Guid projectId, Guid? swarmId) =>
		!_inFlight.TryGetValue(projectId, out var running)
		|| running.Count == 0
		|| (swarmId.HasValue && running.Count == 1 && running.Contains(swarmId));

	public void Take(Guid projectId, Guid? swarmId)
	{
		if (!_inFlight.TryGetValue(projectId, out var running))
		{
			running = new HashSet<Guid?>();
			_inFlight[projectId] = running;
		}

		running.Add(swarmId);
	}
}
