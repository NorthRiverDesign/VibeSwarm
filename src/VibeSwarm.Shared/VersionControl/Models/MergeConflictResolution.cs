namespace VibeSwarm.Shared.VersionControl.Models;

public sealed class MergeConflictResolution
{
	public string FileName { get; set; } = string.Empty;

	public string ResolvedContent { get; set; } = string.Empty;
}
