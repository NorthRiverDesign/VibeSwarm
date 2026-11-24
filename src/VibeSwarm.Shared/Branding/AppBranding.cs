namespace VibeSwarm.Shared.Branding;

/// <summary>
/// Single source of truth for the app's display name. Change <see cref="Name"/>
/// to rebrand. Static assets (index.html, manifest.json, offline.html) are not
/// auto-synced and must be updated alongside this value when renaming.
/// </summary>
public static class AppBranding
{
	public const string Name = "VibeSwarm";
	public const string ShortName = "VibeSwarm";
	public const string Tagline = "AI coding agent orchestrator";

	/// <summary>
	/// Lowercase id for everything an install names on disk and on GitHub: the release archive
	/// (<c>{PackageId}-v1.2.3.tar.gz</c>), the install folder (<c>~/.{PackageId}</c>) and the
	/// systemd service (<c>{PackageId}.service</c>). <c>install.sh</c> and
	/// <c>.github/workflows/release.yml</c> carry the same value; <c>InstallerBrandingTests</c>
	/// fails the build when they drift. Renaming it strands existing installs on the old
	/// service and folder names, so change it before the first public release.
	/// </summary>
	public const string PackageId = "vibeswarm";

	/// <summary>
	/// The GitHub repository (<c>owner/name</c>) whose releases the updater installs. A fork can
	/// point its installs elsewhere with <c>Updates__Repository</c> in <c>.env</c>.
	/// </summary>
	public const string ReleaseRepository = "NorthRiverDesign/VibeSwarm";

	public const string RepositoryUrl = "https://github.com/" + ReleaseRepository;
}
