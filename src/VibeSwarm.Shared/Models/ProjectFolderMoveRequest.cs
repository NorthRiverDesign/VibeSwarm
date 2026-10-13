namespace VibeSwarm.Shared.Models;

/// <summary>
/// Moves a project into a folder, or out of every folder when <see cref="FolderId"/> is null.
/// </summary>
public sealed class ProjectFolderMoveRequest
{
	public Guid? FolderId { get; set; }
}
