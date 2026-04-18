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
}
