namespace VibeSwarm.Shared.VersionControl.Models;

public sealed class MergeConflictFile
{
	public string FileName { get; set; } = string.Empty;

	public string DiffContent { get; set; } = string.Empty;

	public string Content { get; set; } = string.Empty;
}
