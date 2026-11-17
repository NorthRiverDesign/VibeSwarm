using System.Reflection;

namespace VibeSwarm.Web.Services.Updates;

/// <summary>The version stamped into this build (release.yml passes <c>-p:Version</c> from the tag).</summary>
public static class AppVersionInfo
{
	private static readonly Lazy<string> InformationalVersionValue = new(ReadInformationalVersion);

	/// <summary>Such as <c>1.4.0+3f2a9c1</c>: the version and, for builds from git, the commit.</summary>
	public static string InformationalVersion => InformationalVersionValue.Value;

	/// <summary>The version without build metadata, or 0.0.0-dev when it can't be read.</summary>
	public static ReleaseVersion Current => ReleaseVersion.TryParse(InformationalVersion, out var version)
		? version
		: new ReleaseVersion(0, 0, 0, ReleaseVersion.DevelopmentLabel);

	private static string ReadInformationalVersion()
	{
		var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersionInfo).Assembly;
		return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString()
			?? "0.0.0-dev";
	}
}
