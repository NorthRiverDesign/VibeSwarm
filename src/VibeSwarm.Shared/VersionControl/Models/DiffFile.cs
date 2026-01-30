namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// Represents a single file in a git diff with its change statistics
/// </summary>
public class DiffFile
{
	/// <summary>
	/// The file name/path from the diff
	/// </summary>
	public string FileName { get; set; } = string.Empty;

	/// <summary>
	/// The raw diff content for this file
	/// </summary>
	public string DiffContent { get; set; } = string.Empty;

	/// <summary>
	/// Number of lines added
	/// </summary>
	public int Additions { get; set; }

	/// <summary>
	/// Number of lines deleted
	/// </summary>
	public int Deletions { get; set; }

	/// <summary>
	/// Whether this is a newly created file
	/// </summary>
	public bool IsNew { get; set; }

	/// <summary>
	/// Whether this file was deleted
	/// </summary>
	public bool IsDeleted { get; set; }
}
