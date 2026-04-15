namespace VibeSwarm.Shared.Data;

/// <summary>
/// Identifies where a <see cref="Skill"/> originated, so the UI can show provenance
/// and the installer can know which source-specific flow produced it.
/// </summary>
public enum SkillSourceType
{
	/// <summary>
	/// Authored in-app via the Skills page editor. No external source.
	/// </summary>
	Manual = 0,

	/// <summary>
	/// Imported from a Claude-exported <c>.skill</c> ZIP archive.
	/// </summary>
	ZipImport = 1,

	/// <summary>
	/// Installed from the curated <c>github.com/anthropics/skills</c> marketplace.
	/// </summary>
	Marketplace = 2,

	/// <summary>
	/// Installed from a folder on the local filesystem.
	/// </summary>
	LocalPath = 3,
}
