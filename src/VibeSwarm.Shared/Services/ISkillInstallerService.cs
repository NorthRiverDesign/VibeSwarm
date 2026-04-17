using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Installer orchestrator. Dispatches a <see cref="SkillInstallRequest"/> to the strategy
/// matching its <see cref="SkillInstallRequest.Source"/> and returns either a preview (so
/// the UI can render a confirmation screen) or a committed <see cref="SkillInstallResult"/>.
/// </summary>
public interface ISkillInstallerService
{
	/// <summary>
	/// Parses the source without committing anything to the database or skill storage.
	/// Safe to call repeatedly; idempotent and side-effect-free.
	/// </summary>
	Task<SkillInstallPreview> PreviewAsync(SkillInstallRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	/// Parses the source, writes the skill folder into central storage, and creates the
	/// <c>Skill</c> row. Returns a skipped result (without overwriting) when a skill with
	/// the same name already exists.
	/// </summary>
	Task<SkillInstallResult> InstallAsync(SkillInstallRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Strategy interface implemented once per supported <see cref="SkillInstallSource"/>.
/// Each strategy is responsible for turning a raw request into a <see cref="SkillInstallPreview"/>
/// plus a staged on-disk folder path whose layout will be copied verbatim into skill storage.
/// </summary>
public interface ISkillInstaller
{
	SkillInstallSource Source { get; }

	/// <summary>
	/// Parses the request, stages any supporting files to a temp folder, and returns a
	/// preview plus the staged folder path. The caller owns deletion of <paramref name="StagedDirectory"/>.
	/// </summary>
	Task<(SkillInstallPreview Preview, string StagedDirectory)> StageAsync(SkillInstallRequest request, CancellationToken cancellationToken = default);
}
