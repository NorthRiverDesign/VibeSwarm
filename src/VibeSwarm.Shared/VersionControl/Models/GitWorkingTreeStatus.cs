namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// Structured status for the git working tree.
/// </summary>
public sealed class GitWorkingTreeStatus
{
	/// <summary>
	/// Whether the repository currently has uncommitted changes.
	/// </summary>
	public bool HasUncommittedChanges { get; init; }

	/// <summary>
	/// The changed file paths reported for the working tree.
	/// </summary>
	public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

	/// <summary>
	/// The number of changed files in the working tree.
	/// </summary>
	public int ChangedFilesCount { get; init; }
}
