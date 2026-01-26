namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// Represents information about a Git branch.
/// </summary>
public sealed class GitBranchInfo
{
	/// <summary>
	/// The name of the branch (e.g., "main", "feature/new-feature").
	/// For remote branches, this is the full ref name without "remotes/" prefix (e.g., "origin/main").
	/// </summary>
	public string Name { get; init; } = string.Empty;

	/// <summary>
	/// The short name of the branch (e.g., "main" for both local and remote).
	/// For remote branches like "origin/main", this would be "main".
	/// </summary>
	public string ShortName { get; init; } = string.Empty;

	/// <summary>
	/// Whether this is a remote tracking branch.
	/// </summary>
	public bool IsRemote { get; init; }

	/// <summary>
	/// Whether this is the currently checked out branch.
	/// </summary>
	public bool IsCurrent { get; init; }

	/// <summary>
	/// The remote name if this is a remote branch (e.g., "origin").
	/// </summary>
	public string? RemoteName { get; init; }

	/// <summary>
	/// The commit hash that this branch points to.
	/// </summary>
	public string? CommitHash { get; init; }

	public override string ToString() => IsCurrent ? $"* {Name}" : Name;
}
