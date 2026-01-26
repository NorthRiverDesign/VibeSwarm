namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// Summary of git diff statistics.
/// </summary>
public sealed class GitDiffSummary
{
	/// <summary>
	/// Number of files changed.
	/// </summary>
	public int FilesChanged { get; init; }

	/// <summary>
	/// Number of lines inserted.
	/// </summary>
	public int Insertions { get; init; }

	/// <summary>
	/// Number of lines deleted.
	/// </summary>
	public int Deletions { get; init; }

	public override string ToString()
	{
		return $"{FilesChanged} file(s) changed, {Insertions} insertion(s)(+), {Deletions} deletion(s)(-)";
	}
}
