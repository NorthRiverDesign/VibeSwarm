using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Manages transitions for git recovery checkpoints captured during job execution.
/// </summary>
public static class JobCheckpointStateMachine
{
	private static readonly Dictionary<GitCheckpointStatus, HashSet<GitCheckpointStatus>> ValidTransitions = new()
	{
		[GitCheckpointStatus.None] = [GitCheckpointStatus.Protecting, GitCheckpointStatus.Preserved],
		[GitCheckpointStatus.Protecting] = [GitCheckpointStatus.None, GitCheckpointStatus.Preserved],
		[GitCheckpointStatus.Preserved] = [GitCheckpointStatus.Protecting, GitCheckpointStatus.Cleared],
		[GitCheckpointStatus.Cleared] = [GitCheckpointStatus.None, GitCheckpointStatus.Protecting, GitCheckpointStatus.Preserved]
	};

	public static bool CanTransition(GitCheckpointStatus from, GitCheckpointStatus to)
	{
		if (from == to)
		{
			return true;
		}

		return ValidTransitions.TryGetValue(from, out var validTargets) && validTargets.Contains(to);
	}

	public static bool TryTransition(Job job, GitCheckpointStatus newStatus)
	{
		if (!CanTransition(job.GitCheckpointStatus, newStatus))
		{
			return false;
		}

		job.GitCheckpointStatus = newStatus;
		if (newStatus == GitCheckpointStatus.Preserved)
		{
			job.GitCheckpointCapturedAt ??= DateTime.UtcNow;
		}

		return true;
	}
}
