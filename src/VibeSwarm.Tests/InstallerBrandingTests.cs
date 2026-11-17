using System.Text.RegularExpressions;
using VibeSwarm.Shared.Branding;
using VibeSwarm.Web.Services.Updates;

namespace VibeSwarm.Tests;

/// <summary>
/// The app, the installer, the launcher and the release workflow name the same things on disk and on
/// GitHub. These fail when one is renamed without the others.
/// </summary>
public class InstallerBrandingTests
{
	private static readonly string RepositoryRoot = FindRepositoryRoot();

	private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepositoryRoot, .. path]));

	private static string ShellDefault(string script, string variable)
	{
		var match = Regex.Match(script, $@"^{variable}=""(?:\$\{{[A-Z_]+:-)?([^""}}]+)\}}?""", RegexOptions.Multiline);
		Assert.True(match.Success, $"{variable}= not found");
		return match.Groups[1].Value;
	}

	[Fact]
	public void InstallScript_UsesTheAppsNames()
	{
		var script = Read("install.sh");

		Assert.Equal(AppBranding.Name, ShellDefault(script, "APP_NAME"));
		Assert.Equal(AppBranding.PackageId, ShellDefault(script, "APP_ID"));
		Assert.Equal(AppBranding.ReleaseRepository, ShellDefault(script, "REPO"));
		Assert.Contains($"raw.githubusercontent.com/{AppBranding.ReleaseRepository}/main/install.sh", script);
	}

	[Fact]
	public void ReleaseWorkflow_PublishesTheAssetsTheUpdaterLooksFor()
	{
		var workflow = Read(".github", "workflows", "release.yml");

		Assert.Matches($@"(?m)^\s+APP_ID:\s*{Regex.Escape(AppBranding.PackageId)}\s*$", workflow);
		Assert.Contains("name=\"$APP_ID-${{ steps.version.outputs.tag }}\"", workflow);
		Assert.Contains("tar -czf \"$name.tar.gz\"", workflow);
		Assert.Contains($"> {ReleasePackageStager.ChecksumFileName}", workflow);
		Assert.Contains("-p:Version=${{ steps.version.outputs.version }}", workflow);
		Assert.Equal($"{AppBranding.PackageId}-v1.2.0.tar.gz", ReleasePackageStager.GetPackageAssetName("v1.2.0"));
	}

	[Fact]
	public void Launcher_SharesTheAppsStateFilesAndRestartCode()
	{
		var launcher = Read("src", "VibeSwarm.Web", "Deploy", "launcher.sh");

		Assert.Contains($"RESTART_EXIT_CODE={InstallLayout.RestartExitCode}", launcher);
		Assert.Contains($"PENDING=\"$STATE/{InstallLayout.PendingFileName}\"", launcher);
		Assert.Contains($"TRIAL=\"$STATE/{InstallLayout.TrialFileName}\"", launcher);
		Assert.Contains($"LAST_UPDATE=\"$STATE/{InstallLayout.LastUpdateFileName}\"", launcher);
		Assert.Contains("Updates__InstallRoot=\"$ROOT\"", launcher);
		Assert.Contains("--contentRoot", launcher);
	}

	[Fact]
	public void LauncherIsPublishedWithTheApp()
	{
		var project = Read("src", "VibeSwarm.Web", "VibeSwarm.Web.csproj");

		Assert.Contains("<None Update=\"Deploy/launcher.sh\" CopyToPublishDirectory=\"PreserveNewest\" />", project);
		Assert.Contains("$VERSIONS/$VERSION/Deploy/launcher.sh", Read("install.sh"));
	}

	[Fact]
	public void ShellScripts_KeepUnixLineEndings()
	{
		Assert.Contains("*.sh text eol=lf", Read(".gitattributes"));
		Assert.DoesNotContain('\r', Read("install.sh"));
		Assert.DoesNotContain('\r', Read("src", "VibeSwarm.Web", "Deploy", "launcher.sh"));
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VibeSwarm.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
	}
}
