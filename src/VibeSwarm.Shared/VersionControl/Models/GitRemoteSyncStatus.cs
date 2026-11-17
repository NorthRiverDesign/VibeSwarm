namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// How the checked-out branch compares with its counterpart on the remote.
/// </summary>
public sealed class GitRemoteSyncStatus
{
	/// <summary>
	/// The remote branch compared against (e.g., "origin/main"). Null when HEAD is detached
	/// or the branch doesn't exist on the remote.
	/// </summary>
	public string? RemoteBranch { get; init; }

	/// <summary>
	/// Commits on the remote branch that the checkout doesn't have. Null when the remote has
	/// commits that haven't been fetched yet, so they can't be counted.
	/// </summary>
	public int? CommitsBehind { get; init; }

	/// <summary>
	/// Whether the remote was reached. When false, the count comes from the last fetch.
	/// </summary>
	public bool IsRemoteReachable { get; init; }

	/// <summary>
	/// Whether the remote branch has commits the checkout doesn't.
	/// </summary>
	public bool IsBehind => RemoteBranch != null && CommitsBehind != 0;
}
