using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Owns the server-side on-disk storage for installed skills. Skills are materialized once
/// under a central root (<c>&lt;LocalApplicationData&gt;/VibeSwarm/skills/&lt;id&gt;/</c> by default)
/// and agents receive the absolute path to <c>SKILL.md</c> via the system prompt.
/// </summary>
public interface ISkillStorageService
{
	/// <summary>
	/// Absolute path to the root folder containing all installed skill directories.
	/// Respects the <c>VIBESWARM_SKILLS_PATH</c> environment variable override when set.
	/// </summary>
	string RootPath { get; }

	/// <summary>
	/// Returns the deterministic on-disk folder for the given skill id.
	/// The folder is not guaranteed to exist; call <see cref="EnsureMaterializedAsync"/> first.
	/// </summary>
	string GetSkillDirectory(Guid skillId);

	/// <summary>
	/// Returns the absolute path to the skill's <c>SKILL.md</c>. Does not guarantee the file exists.
	/// </summary>
	string GetSkillManifestPath(Guid skillId);

	/// <summary>
	/// Makes sure the skill's folder and <c>SKILL.md</c> exist on disk. For skills that were
	/// installed with a <see cref="Skill.StoragePath"/> this is a no-op. For legacy skills that
	/// only have <see cref="Skill.Content"/> in the database (no StoragePath), the content is
	/// written out once and the skill is updated with the new path.
	/// Returns the absolute storage folder.
	/// </summary>
	Task<string> EnsureMaterializedAsync(Skill skill, CancellationToken cancellationToken = default);

	/// <summary>
	/// Copies the contents of <paramref name="sourceDirectory"/> into the skill's storage folder.
	/// The destination is created fresh; any existing folder for that skill id is replaced.
	/// Used by installers after preview/confirm to commit a skill into central storage.
	/// Returns the absolute destination folder.
	/// </summary>
	Task<string> MaterializeFromDirectoryAsync(Guid skillId, string sourceDirectory, CancellationToken cancellationToken = default);

	/// <summary>
	/// Removes the skill's folder and all contents. Safe to call if the folder does not exist.
	/// </summary>
	void Delete(Guid skillId);
}
