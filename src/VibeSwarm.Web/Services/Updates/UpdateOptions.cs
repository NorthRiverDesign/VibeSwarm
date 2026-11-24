using VibeSwarm.Shared.Branding;

namespace VibeSwarm.Web.Services.Updates;

/// <summary>
/// The <c>Updates</c> section, set with <c>Updates__*</c> lines in <c>.env</c>. The installer's
/// launcher sets <see cref="InstallRoot"/>; nothing else needs setting.
/// </summary>
public class UpdateOptions
{
	public const string SectionName = "Updates";

	/// <summary>The <c>owner/name</c> GitHub repository to take releases from.</summary>
	public string Repository { get; set; } = AppBranding.ReleaseRepository;

	/// <summary>Look for a new release in the background. The Settings screen can always check.</summary>
	public bool AutoCheck { get; set; } = true;

	public int CheckIntervalHours { get; set; } = 12;

	/// <summary>Offer pre-releases (tags such as <c>v1.2.0-beta.1</c>) as well as releases.</summary>
	public bool IncludePrereleases { get; set; }

	/// <summary>
	/// The folder install.sh created, holding <c>launcher.sh</c>, <c>versions/</c>, <c>data/</c>
	/// and <c>state/</c>. Set by the launcher; empty means a manual install, which can check for
	/// updates but not install them.
	/// </summary>
	public string? InstallRoot { get; set; }

	/// <summary>Where the GitHub REST API lives. Only tests change it.</summary>
	public string ApiBaseUrl { get; set; } = "https://api.github.com/";
}
