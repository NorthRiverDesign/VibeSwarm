namespace VibeSwarm.Web.Services.Updates;

/// <summary>
/// The folder install.sh lays out, which the launcher (<c>Deploy/launcher.sh</c>) runs:
/// <code>
/// launcher.sh          started by the service; applies staged updates and rolls back failed ones
/// current -&gt; versions/1.4.0
/// versions/1.4.0/      one folder per release, never changed after it is staged
/// data/                working directory: .env, the SQLite database, the HTTPS certificate
/// state/               pending.env, trial.env and last-update.env, shared with the launcher
/// backups/             the SQLite database as it was before each update
/// </code>
/// </summary>
public sealed class InstallLayout
{
	public const string LauncherFileName = "launcher.sh";
	public const string PendingFileName = "pending.env";
	public const string TrialFileName = "trial.env";
	public const string LastUpdateFileName = "last-update.env";

	/// <summary>The exit code that asks the launcher to restart the app (after staging an update).</summary>
	public const int RestartExitCode = 75;

	/// <summary>Prefix of folders being downloaded or extracted under <c>versions/</c>.</summary>
	public const string StagingPrefix = ".staging-";

	private InstallLayout(string root)
	{
		Root = root;
	}

	public string Root { get; }
	public string Launcher => Path.Combine(Root, LauncherFileName);
	public string VersionsDirectory => Path.Combine(Root, "versions");
	public string DataDirectory => Path.Combine(Root, "data");
	public string StateDirectory => Path.Combine(Root, "state");
	public string BackupsDirectory => Path.Combine(Root, "backups");
	public string CurrentLink => Path.Combine(Root, "current");
	public string PendingFile => Path.Combine(StateDirectory, PendingFileName);
	public string TrialFile => Path.Combine(StateDirectory, TrialFileName);
	public string LastUpdateFile => Path.Combine(StateDirectory, LastUpdateFileName);

	public string GetVersionDirectory(string version) => Path.Combine(VersionsDirectory, version);

	/// <summary>
	/// The layout this process runs from, or null with the reason when it wasn't started by the
	/// installer's launcher (a source build, a hand-copied release, or Windows).
	/// </summary>
	public static InstallLayout? TryResolve(string? installRoot, string appBaseDirectory, out string? reason)
	{
		if (OperatingSystem.IsWindows())
		{
			reason = "One-click updates need the Linux or macOS installer. Download the new release from GitHub instead.";
			return null;
		}

		if (string.IsNullOrWhiteSpace(installRoot))
		{
			reason = "This copy wasn't set up by the installer, so it updates by hand. Re-run install.sh to switch to one-click updates.";
			return null;
		}

		var root = Path.GetFullPath(installRoot.Trim());
		var layout = new InstallLayout(root);
		var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appBaseDirectory));
		var versionsDirectory = Path.TrimEndingDirectorySeparator(layout.VersionsDirectory);

		if (!File.Exists(layout.Launcher))
		{
			reason = $"The installer's launcher is missing from {root}. Re-run install.sh to repair it.";
			return null;
		}

		if (!string.Equals(Path.GetDirectoryName(baseDirectory), versionsDirectory, StringComparison.Ordinal))
		{
			reason = $"This app isn't running from {versionsDirectory}. Re-run install.sh to repair the install.";
			return null;
		}

		reason = null;
		return layout;
	}

	/// <summary>The release folder this process runs from (its name is the version).</summary>
	public static string GetRunningVersionName(string appBaseDirectory) =>
		Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(appBaseDirectory)));
}

/// <summary>
/// <c>KEY=value</c> files shared with the launcher. Values run to the end of the line, so they can
/// hold spaces but never newlines. Bash reads them line by line and never <c>source</c>s them.
/// </summary>
public static class KeyValueFile
{
	public static Dictionary<string, string> Read(string path)
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!File.Exists(path))
		{
			return values;
		}

		foreach (var line in File.ReadAllLines(path))
		{
			var separator = line.IndexOf('=');
			if (separator <= 0)
			{
				continue;
			}

			values[line[..separator].Trim()] = line[(separator + 1)..].TrimEnd('\r');
		}

		return values;
	}

	/// <summary>Writes beside the target and renames over it, so a reader never sees half a file.</summary>
	public static void Write(string path, IEnumerable<KeyValuePair<string, string?>> values)
	{
		var lines = new List<string>();
		foreach (var (key, value) in values)
		{
			var text = value ?? string.Empty;
			if (key.Length == 0 || key.Contains('=') || key.Any(char.IsWhiteSpace) || text.Contains('\n') || text.Contains('\r'))
			{
				throw new ArgumentException($"'{key}' can't be written to {Path.GetFileName(path)}.");
			}

			lines.Add($"{key}={text}");
		}

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
		File.WriteAllText(temporaryPath, string.Join('\n', lines) + "\n");
		File.Move(temporaryPath, path, overwrite: true);
	}
}
