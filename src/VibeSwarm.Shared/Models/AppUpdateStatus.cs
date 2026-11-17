namespace VibeSwarm.Shared.Models;

/// <summary>
/// What the Settings screen shows about this install's version and the newest release on GitHub.
/// </summary>
public class AppUpdateStatus
{
	/// <summary>The running build, such as <c>1.4.0</c>. Unstamped source builds report <c>0.0.0-dev</c>.</summary>
	public string CurrentVersion { get; set; } = string.Empty;

	/// <summary>False for a build that wasn't stamped from a release tag; it is never offered an update.</summary>
	public bool IsReleaseBuild { get; set; }

	/// <summary>The repository releases come from (<c>owner/name</c>).</summary>
	public string Repository { get; set; } = string.Empty;

	public AppInstallKind InstallKind { get; set; }

	/// <summary>Why one-click install isn't available here, when it isn't.</summary>
	public string? InstallUnavailableReason { get; set; }

	public AppReleaseSummary? LatestRelease { get; set; }

	public bool IsUpdateAvailable { get; set; }

	public DateTime? LastCheckedAtUtc { get; set; }

	/// <summary>Why the last check found nothing, such as being offline. Not an error the user must act on.</summary>
	public string? CheckMessage { get; set; }

	public AppUpdateStage Stage { get; set; }

	/// <summary>The current step in words while an update runs, or why it stopped.</summary>
	public string? StageMessage { get; set; }

	/// <summary>Download progress, 0 to 100, while <see cref="Stage"/> is Downloading.</summary>
	public int? ProgressPercent { get; set; }

	/// <summary>The version being installed while an update runs.</summary>
	public string? TargetVersion { get; set; }

	/// <summary>Changes on every start, so the page can tell the restarted app from the old one.</summary>
	public string ServerInstanceId { get; set; } = string.Empty;

	/// <summary>True when the database is SQLite, which the updater backs up and restores on rollback.</summary>
	public bool DatabaseBackedUp { get; set; }

	/// <summary>The outcome of the last update the launcher applied, shown until the next one.</summary>
	public AppUpdateResult? LastResult { get; set; }

	public bool CanInstall => InstallKind == AppInstallKind.Managed && IsUpdateAvailable && !IsBusy;

	public bool IsBusy => Stage is AppUpdateStage.Downloading or AppUpdateStage.Verifying
		or AppUpdateStage.WaitingForJobs or AppUpdateStage.Restarting;
}

public class AppReleaseSummary
{
	public string Version { get; set; } = string.Empty;
	public string Tag { get; set; } = string.Empty;
	public string? Name { get; set; }
	public string? Notes { get; set; }
	public string? HtmlUrl { get; set; }
	public DateTime? PublishedAtUtc { get; set; }
	public bool IsPrerelease { get; set; }
}

public class AppUpdateResult
{
	public bool Succeeded { get; set; }
	public string? FromVersion { get; set; }
	public string? ToVersion { get; set; }
	public string? Message { get; set; }
	public DateTime? AtUtc { get; set; }
}

public enum AppInstallKind
{
	/// <summary>Built from a checkout or copied by hand: updates are manual.</summary>
	Manual,

	/// <summary>Set up by install.sh and run by its launcher: one-click updates work.</summary>
	Managed
}

public enum AppUpdateStage
{
	Idle,
	Downloading,
	Verifying,
	WaitingForJobs,
	Restarting,
	Failed
}
