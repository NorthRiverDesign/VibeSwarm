namespace VibeSwarm.Shared.Data;

/// <summary>
/// Describes where successful job changes should end up after execution.
/// </summary>
public enum GitChangeDeliveryMode
{
	/// <summary>
	/// Keep changes on the working branch.
	/// </summary>
	CommitToBranch = 0,

	/// <summary>
	/// Push changes to a source branch and open a pull request into a target branch.
	/// </summary>
	PullRequest = 1
}
