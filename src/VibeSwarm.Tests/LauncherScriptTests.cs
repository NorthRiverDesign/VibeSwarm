using System.Diagnostics;
using VibeSwarm.Web.Services.Updates;

namespace VibeSwarm.Tests;

/// <summary>
/// Runs Deploy/launcher.sh against stub releases: a fake dotnet host does whatever a "behavior" file
/// beside each release's dll says (stage an update, start fine, crash, hang).
/// </summary>
public sealed class LauncherScriptTests : IDisposable
{
	private const string StubHost = """
		#!/usr/bin/env bash
		dll="$1"; dir="$(dirname "$dll")"; ver="$(basename "$dir")"; root="$Updates__InstallRoot"
		echo "$ver" >> "$root/runs.log"
		behavior="$(cat "$dir/behavior")"
		# A release that stages an update behaves normally once it has done so.
		echo ok > "$dir/behavior"
		case "$behavior" in
		  ok) if grep -qx "VERSION=$ver" "$root/state/trial.env" 2>/dev/null; then rm "$root/state/trial.env"; fi; exit 0 ;;
		  crash) exit 3 ;;
		  hang) trap 'exit 0' TERM; while :; do sleep 0.1; done ;;
		  update:*) echo "written-by-$ver" > "$root/data/app.db"
		    printf 'VERSION=%s\nFROM=%s\nSQLITE_PATH=%s\n' "${behavior#update:}" "$ver" "$root/data/app.db" > "$root/state/pending.env"
		    exit 75 ;;
		esac
		""";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"launcher-{Guid.NewGuid():N}");

	public LauncherScriptTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "dotnet"));
		Directory.CreateDirectory(Path.Combine(_root, "data"));
		var host = Path.Combine(_root, "dotnet", "dotnet");
		File.WriteAllText(host, StubHost.ReplaceLineEndings("\n") + "\n");
		File.Copy(Path.Combine(FindRepositoryRoot(), "src", "VibeSwarm.Web", "Deploy", "launcher.sh"), Path.Combine(_root, InstallLayout.LauncherFileName));
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private void AddRelease(string version, string behavior)
	{
		var directory = Path.Combine(_root, "versions", version);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "App.runtimeconfig.json"), ReleasePackageBuilder.RuntimeConfig);
		File.WriteAllText(Path.Combine(directory, "App.dll"), string.Empty);
		File.WriteAllText(Path.Combine(directory, "behavior"), behavior);
	}

	private async Task<int> RunLauncherAsync(int confirmTimeoutSeconds = 300)
	{
		var start = new ProcessStartInfo("bash", [Path.Combine(_root, InstallLayout.LauncherFileName)])
		{
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false
		};
		start.Environment["UPDATE_CONFIRM_TIMEOUT"] = confirmTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
		using var process = Process.Start(start)!;
		var stderr = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		await process.WaitForExitAsync(timeout.Token);
		await stderr;
		return process.ExitCode;
	}

	private string CurrentTarget() => new FileInfo(Path.Combine(_root, "current")).LinkTarget!;
	private string[] Runs() => File.ReadAllLines(Path.Combine(_root, "runs.log"));
	private string Database() => File.ReadAllText(Path.Combine(_root, "data", "app.db")).Trim();
	private Dictionary<string, string> LastUpdate() => KeyValueFile.Read(Path.Combine(_root, "state", InstallLayout.LastUpdateFileName));

	private static bool CanRunBash() => !OperatingSystem.IsWindows() && File.Exists("/bin/bash");

	[Fact]
	public async Task StagedUpdate_SwitchesVersionsAndBacksUpTheDatabase()
	{
		if (!CanRunBash()) return;
		AddRelease("1.0.0", "update:1.1.0");
		AddRelease("1.1.0", "ok");
		File.CreateSymbolicLink(Path.Combine(_root, "current"), "versions/1.0.0");

		var exitCode = await RunLauncherAsync();

		Assert.Equal(0, exitCode);
		Assert.Equal(["1.0.0", "1.1.0"], Runs());
		Assert.Equal("versions/1.1.0", CurrentTarget());
		Assert.False(File.Exists(Path.Combine(_root, "state", InstallLayout.TrialFileName)));
		Assert.False(File.Exists(Path.Combine(_root, "state", InstallLayout.PendingFileName)));
		var backup = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "backups")));
		Assert.Equal("written-by-1.0.0", File.ReadAllText(Path.Combine(backup, "app.db")).Trim());
	}

	[Fact]
	public async Task UpdateThatCrashesOnStart_IsRolledBackWithItsDatabase()
	{
		if (!CanRunBash()) return;
		AddRelease("1.0.0", "update:1.1.0");
		AddRelease("1.1.0", "crash");
		File.CreateSymbolicLink(Path.Combine(_root, "current"), "versions/1.0.0");

		var exitCode = await RunLauncherAsync();

		Assert.Equal(0, exitCode);
		Assert.Equal(["1.0.0", "1.1.0", "1.0.0"], Runs());
		Assert.Equal("versions/1.0.0", CurrentTarget());
		Assert.Equal("written-by-1.0.0", Database());
		var result = LastUpdate();
		Assert.Equal("rolled-back", result["RESULT"]);
		Assert.Equal("1.0.0", result["FROM"]);
		Assert.Equal("1.1.0", result["TO"]);
		Assert.Contains("exited with code 3", result["MESSAGE"]);
		Assert.False(File.Exists(Path.Combine(_root, "state", InstallLayout.TrialFileName)));
	}

	[Fact]
	public async Task UpdateThatNeverFinishesStarting_IsStoppedAndRolledBack()
	{
		if (!CanRunBash()) return;
		AddRelease("1.0.0", "update:1.1.0");
		AddRelease("1.1.0", "hang");
		File.CreateSymbolicLink(Path.Combine(_root, "current"), "versions/1.0.0");

		var exitCode = await RunLauncherAsync(confirmTimeoutSeconds: 2);

		Assert.Equal(0, exitCode);
		Assert.Equal("versions/1.0.0", CurrentTarget());
		Assert.Contains("didn't finish starting", LastUpdate()["MESSAGE"]);
	}

	[Fact]
	public async Task IncompleteStagedRelease_IsNotSwitchedTo()
	{
		if (!CanRunBash()) return;
		AddRelease("1.0.0", "update:1.1.0");
		Directory.CreateDirectory(Path.Combine(_root, "versions", "1.1.0"));
		File.CreateSymbolicLink(Path.Combine(_root, "current"), "versions/1.0.0");

		await RunLauncherAsync();

		Assert.Equal("versions/1.0.0", CurrentTarget());
		Assert.Equal("failed", LastUpdate()["RESULT"]);
		Assert.Equal(["1.0.0", "1.0.0"], Runs());
	}

	[Fact]
	public async Task TrialThatKeepsGettingInterrupted_IsRolledBackOnTheNextStart()
	{
		if (!CanRunBash()) return;
		AddRelease("1.0.0", "ok");
		AddRelease("1.1.0", "ok");
		File.CreateSymbolicLink(Path.Combine(_root, "current"), "versions/1.1.0");
		Directory.CreateDirectory(Path.Combine(_root, "state"));
		KeyValueFile.Write(Path.Combine(_root, "state", InstallLayout.TrialFileName),
			[new("VERSION", "1.1.0"), new("PREVIOUS", "1.0.0"), new("BACKUP", ""), new("SQLITE_PATH", ""), new("STARTS", "2")]);

		await RunLauncherAsync();

		Assert.Equal(["1.0.0"], Runs());
		Assert.Equal("versions/1.0.0", CurrentTarget());
		Assert.Equal("rolled-back", LastUpdate()["RESULT"]);
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
