using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services.Updates;

/// <summary>
/// Checks GitHub for releases in the background and, when asked, installs the newest one:
/// <list type="number">
/// <item>stage it in <c>versions/&lt;version&gt;</c> (download, SHA-256, extract, version and runtime checks);</item>
/// <item>pause the queue and wait until no job is running or waiting for an answer;</item>
/// <item>write <c>state/pending.env</c> and exit with <see cref="InstallLayout.RestartExitCode"/>.</item>
/// </list>
/// The launcher then backs up the SQLite database, points <c>current</c> at the new version and
/// starts it. The new version confirms itself here once it is serving; if it exits or hangs first,
/// the launcher restores the database and the previous version.
/// </summary>
public sealed class AppUpdateService : BackgroundService, IAppUpdateService
{
	/// <summary>Starts the queue pause reason the updater sets, so it never resumes a pause the user set.</summary>
	public const string QueuePauseReasonPrefix = "Paused while updating";

	private static readonly JobStatus[] ActiveJobStatuses =
		[JobStatus.Started, JobStatus.Processing, JobStatus.Planning, JobStatus.Paused];

	private static readonly TimeSpan MinimumManualCheckInterval = TimeSpan.FromSeconds(30);

	private readonly IOptionsMonitor<UpdateOptions> _options;
	private readonly IGitHubReleaseClient _releaseClient;
	private readonly IReleasePackageStager _stager;
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IHostApplicationLifetime _lifetime;
	private readonly ILogger<AppUpdateService> _logger;
	private readonly string _appBaseDirectory;
	private readonly ReleaseVersion _currentVersion;
	private readonly InstallLayout? _layout;
	private readonly string? _installUnavailableReason;
	private readonly string _serverInstanceId = Guid.NewGuid().ToString("N");
	private readonly SemaphoreSlim _checkLock = new(1, 1);
	private readonly object _sync = new();

	private GitHubRelease? _latestRelease;
	private ReleaseVersion? _latestVersion;
	private DateTime? _lastCheckedAtUtc;
	private string? _checkMessage;
	private AppUpdateStage _stage = AppUpdateStage.Idle;
	private string? _stageMessage;
	private int? _progressPercent;
	private string? _targetVersion;
	private AppUpdateResult? _lastResult;
	private bool? _databaseIsSqlite;
	private CancellationTokenSource? _updateCancellation;

	public AppUpdateService(
		IOptionsMonitor<UpdateOptions> options,
		IGitHubReleaseClient releaseClient,
		IReleasePackageStager stager,
		IServiceScopeFactory scopeFactory,
		IHostApplicationLifetime lifetime,
		ILogger<AppUpdateService> logger,
		string? appBaseDirectory = null,
		ReleaseVersion? currentVersion = null)
	{
		_options = options;
		_releaseClient = releaseClient;
		_stager = stager;
		_scopeFactory = scopeFactory;
		_lifetime = lifetime;
		_logger = logger;
		_appBaseDirectory = appBaseDirectory ?? AppContext.BaseDirectory;
		_currentVersion = currentVersion ?? AppVersionInfo.Current;
		_layout = InstallLayout.TryResolve(options.CurrentValue.InstallRoot, _appBaseDirectory, out _installUnavailableReason);
	}

	internal InstallLayout? Layout => _layout;

	public Task<AppUpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(BuildStatus());

	public async Task<AppUpdateStatus> CheckNowAsync(CancellationToken cancellationToken = default)
	{
		await CheckForUpdatesAsync(force: false, cancellationToken);
		return BuildStatus();
	}

	public Task<AppUpdateStatus> StartUpdateAsync(CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			if (IsBusyLocked())
			{
				return Task.FromResult(BuildStatusLocked());
			}

			var refusal = GetInstallRefusalLocked();
			if (refusal is not null)
			{
				// Only this answer carries the refusal; nothing started, so nothing changes for anyone else.
				var refused = BuildStatusLocked();
				refused.Stage = AppUpdateStage.Failed;
				refused.StageMessage = refusal;
				return Task.FromResult(refused);
			}

			var release = _latestRelease!;
			var version = _latestVersion!;
			_updateCancellation?.Dispose();
			_updateCancellation = new CancellationTokenSource();
			_stage = AppUpdateStage.Downloading;
			_stageMessage = $"Downloading {version}…";
			_progressPercent = 0;
			_targetVersion = version.ToString();
			var token = _updateCancellation.Token;
			_ = Task.Run(() => RunUpdateAsync(release, version, token), CancellationToken.None);
			return Task.FromResult(BuildStatusLocked());
		}
	}

	public Task<AppUpdateStatus> CancelUpdateAsync(CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			if (_stage is AppUpdateStage.Downloading or AppUpdateStage.Verifying or AppUpdateStage.WaitingForJobs)
			{
				_updateCancellation?.Cancel();
				_stageMessage = "Cancelling…";
			}
			else if (_stage == AppUpdateStage.Failed)
			{
				// Dismisses the last failure.
				_stage = AppUpdateStage.Idle;
				_stageMessage = null;
			}

			return Task.FromResult(BuildStatusLocked());
		}
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			await WaitForApplicationStartedAsync(stoppingToken);
			await ReconcileAfterStartAsync(stoppingToken);

			// Leave the first minute to startup work, then check on the configured interval.
			await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
			while (!stoppingToken.IsCancellationRequested)
			{
				var options = _options.CurrentValue;
				if (options.AutoCheck)
				{
					await CheckForUpdatesAsync(force: true, stoppingToken);
				}

				await Task.Delay(TimeSpan.FromHours(Math.Clamp(options.CheckIntervalHours, 1, 24 * 7)), stoppingToken);
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "The update checker stopped unexpectedly.");
		}
	}

	private Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
	{
		if (_lifetime.ApplicationStarted.IsCancellationRequested)
		{
			return Task.CompletedTask;
		}

		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var registration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
		var cancellation = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
		return started.Task.ContinueWith(task =>
		{
			registration.Dispose();
			cancellation.Dispose();
			return task;
		}, TaskScheduler.Default).Unwrap();
	}

	/// <summary>
	/// Runs once the app is serving: confirms a version the launcher is trying out, reads the last
	/// update's outcome, resumes a queue the updater paused and removes old release folders.
	/// </summary>
	internal async Task ReconcileAfterStartAsync(CancellationToken cancellationToken)
	{
		if (_layout is not null)
		{
			try
			{
				ConfirmTrial(_layout);
				LoadLastResult(_layout);
				CleanUpOldReleases(_layout);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Couldn't finish the update bookkeeping in {Root}", _layout.Root);
			}
		}

		await ResumeQueueIfUpdaterPausedItAsync(cancellationToken);
	}

	private void ConfirmTrial(InstallLayout layout)
	{
		var trial = KeyValueFile.Read(layout.TrialFile);
		if (!trial.TryGetValue("VERSION", out var trialVersion))
		{
			return;
		}

		var runningVersion = InstallLayout.GetRunningVersionName(_appBaseDirectory);
		if (!string.Equals(trialVersion, runningVersion, StringComparison.Ordinal))
		{
			_logger.LogWarning("The launcher is trying {TrialVersion}, but {RunningVersion} is running; leaving it to the launcher.",
				trialVersion, runningVersion);
			return;
		}

		KeyValueFile.Write(layout.LastUpdateFile,
		[
			new("RESULT", "succeeded"),
			new("FROM", trial.GetValueOrDefault("PREVIOUS")),
			new("TO", trialVersion),
			new("MESSAGE", $"Updated to {trialVersion}."),
			new("AT", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
		]);
		File.Delete(layout.TrialFile);
		_logger.LogInformation("Update to {Version} confirmed: it started and is serving.", trialVersion);
	}

	private void LoadLastResult(InstallLayout layout)
	{
		var values = KeyValueFile.Read(layout.LastUpdateFile);
		if (!values.TryGetValue("RESULT", out var result))
		{
			return;
		}

		DateTime? at = long.TryParse(values.GetValueOrDefault("AT"), out var seconds)
			? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
			: null;
		lock (_sync)
		{
			_lastResult = new AppUpdateResult
			{
				Succeeded = result == "succeeded",
				FromVersion = values.GetValueOrDefault("FROM"),
				ToVersion = values.GetValueOrDefault("TO"),
				Message = values.GetValueOrDefault("MESSAGE"),
				AtUtc = at
			};
		}
	}

	private void CleanUpOldReleases(InstallLayout layout)
	{
		if (File.Exists(layout.TrialFile) || File.Exists(layout.PendingFile) || !Directory.Exists(layout.VersionsDirectory))
		{
			return;
		}

		// Keep the running release and the one before it, which the launcher rolls back to.
		var keep = new HashSet<string>(StringComparer.Ordinal) { InstallLayout.GetRunningVersionName(_appBaseDirectory) };
		if (KeyValueFile.Read(layout.LastUpdateFile).GetValueOrDefault("FROM") is { Length: > 0 } previous)
		{
			keep.Add(previous);
		}

		foreach (var directory in Directory.EnumerateDirectories(layout.VersionsDirectory))
		{
			var name = Path.GetFileName(directory);
			if (keep.Contains(name))
			{
				continue;
			}

			if (!name.StartsWith(InstallLayout.StagingPrefix, StringComparison.Ordinal) && !ReleaseVersion.TryParse(name, out _))
			{
				continue;
			}

			TryDelete(directory);
		}

		if (Directory.Exists(layout.BackupsDirectory))
		{
			foreach (var backup in Directory.EnumerateDirectories(layout.BackupsDirectory)
				.OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
				.Skip(3))
			{
				TryDelete(backup);
			}
		}
	}

	private void TryDelete(string directory)
	{
		try
		{
			Directory.Delete(directory, recursive: true);
			_logger.LogInformation("Removed old release folder {Path}", directory);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Couldn't remove {Path}", directory);
		}
	}

	private async Task ResumeQueueIfUpdaterPausedItAsync(CancellationToken cancellationToken)
	{
		try
		{
			using var scope = _scopeFactory.CreateScope();
			var queue = scope.ServiceProvider.GetRequiredService<IJobQueueControlService>();
			var state = await queue.GetStateAsync(cancellationToken);
			if (state.IsPaused && state.PausedReason?.StartsWith(QueuePauseReasonPrefix, StringComparison.Ordinal) == true)
			{
				await queue.ResumeAsync(cancellationToken);
				_logger.LogInformation("Resumed the job queue the updater paused.");
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogWarning(ex, "Couldn't resume the job queue after an update.");
		}
	}

	internal async Task CheckForUpdatesAsync(bool force, CancellationToken cancellationToken)
	{
		if (!force && _lastCheckedAtUtc is DateTime last && DateTime.UtcNow - last < MinimumManualCheckInterval)
		{
			return;
		}

		if (!await _checkLock.WaitAsync(0, cancellationToken))
		{
			// A check is already running; wait for it rather than asking GitHub twice.
			await _checkLock.WaitAsync(cancellationToken);
			_checkLock.Release();
			return;
		}

		try
		{
			var options = _options.CurrentValue;
			var lookup = await _releaseClient.GetLatestReleaseAsync(options.Repository, options.IncludePrereleases, cancellationToken);
			lock (_sync)
			{
				_lastCheckedAtUtc = DateTime.UtcNow;
				if (lookup.Release is null)
				{
					_checkMessage = lookup.Message;
					return;
				}

				if (!ReleaseVersion.TryParse(lookup.Release.TagName, out var version))
				{
					_checkMessage = $"The newest release's tag ({lookup.Release.TagName}) isn't a version number.";
					return;
				}

				_latestRelease = lookup.Release;
				_latestVersion = version;
				_checkMessage = null;
			}
		}
		finally
		{
			_checkLock.Release();
		}
	}

	private async Task RunUpdateAsync(GitHubRelease release, ReleaseVersion version, CancellationToken cancellationToken)
	{
		var layout = _layout!;
		string? stagedDirectory = null;
		try
		{
			var progress = new Progress<int>(percent => SetStage(AppUpdateStage.Downloading, $"Downloading {version}… {percent}%", percent));
			// Progress<T> posts to the thread pool; report the verify step only after the download is done.
			stagedDirectory = await _stager.StageAsync(release, version, layout, progress, cancellationToken);
			SetStage(AppUpdateStage.Verifying, $"{version} downloaded and verified.");

			await PauseQueueAsync(version, cancellationToken);
			await WaitForActiveJobsAsync(cancellationToken);

			var sqlitePath = ResolveSqlitePath();
			KeyValueFile.Write(layout.PendingFile,
			[
				new("VERSION", version.ToString()),
				new("FROM", InstallLayout.GetRunningVersionName(_appBaseDirectory)),
				new("SQLITE_PATH", sqlitePath)
			]);

			// The exit code is set before anyone can see Restarting, so the launcher always gets it.
			Environment.ExitCode = InstallLayout.RestartExitCode;
			SetStage(AppUpdateStage.Restarting, $"Restarting into {version}…");
			_logger.LogWarning("Restarting to install {Version}.", version);
			_lifetime.StopApplication();
		}
		catch (Exception ex)
		{
			var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
			if (!cancelled)
			{
				_logger.LogWarning(ex, "Update to {Version} stopped.", version);
			}

			if (stagedDirectory is not null &&
				!string.Equals(Path.GetFileName(stagedDirectory), InstallLayout.GetRunningVersionName(_appBaseDirectory), StringComparison.Ordinal))
			{
				try
				{
					Directory.Delete(stagedDirectory, recursive: true);
				}
				catch (Exception deleteException)
				{
					_logger.LogWarning(deleteException, "Couldn't remove the staged release {Path}", stagedDirectory);
				}
			}

			// Even when the pause call itself was interrupted; only a pause with the updater's reason is lifted.
			await ResumeQueueIfUpdaterPausedItAsync(CancellationToken.None);

			lock (_sync)
			{
				_stage = cancelled ? AppUpdateStage.Idle : AppUpdateStage.Failed;
				_stageMessage = cancelled ? null : ex is UpdateException ? ex.Message : $"The update stopped: {ex.Message}";
				_progressPercent = null;
				_targetVersion = null;
			}
		}
	}

	/// <summary>Pauses the queue unless the user already has.</summary>
	private async Task PauseQueueAsync(ReleaseVersion version, CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var queue = scope.ServiceProvider.GetRequiredService<IJobQueueControlService>();
		var state = await queue.GetStateAsync(cancellationToken);
		if (!state.IsPaused)
		{
			await queue.PauseAsync($"{QueuePauseReasonPrefix} to {version}", cancelRunningJobs: false, cancellationToken);
		}
	}

	private async Task WaitForActiveJobsAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			using (var scope = _scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
				var counts = await db.Jobs
					.Where(job => ActiveJobStatuses.Contains(job.Status))
					.GroupBy(job => job.Status)
					.Select(group => new { Status = group.Key, Count = group.Count() })
					.ToListAsync(cancellationToken);
				var running = counts.Where(c => c.Status != JobStatus.Paused).Sum(c => c.Count);
				var waiting = counts.Where(c => c.Status == JobStatus.Paused).Sum(c => c.Count);
				if (running == 0 && waiting == 0)
				{
					return;
				}

				var parts = new List<string>();
				if (running > 0) parts.Add(running == 1 ? "1 running job to finish" : $"{running} running jobs to finish");
				if (waiting > 0) parts.Add(waiting == 1 ? "1 job waiting for your answer" : $"{waiting} jobs waiting for your answer");
				SetStage(AppUpdateStage.WaitingForJobs, $"Waiting for {string.Join(" and ", parts)}. The queue is paused.");
			}

			await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
		}
	}

	/// <summary>The SQLite database file, which the launcher backs up before switching versions.</summary>
	private string? ResolveSqlitePath()
	{
		using var scope = _scopeFactory.CreateScope();
		var path = GetSqlitePath(scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>());
		_databaseIsSqlite = path is not null;
		return path;
	}

	private static string? GetSqlitePath(VibeSwarmDbContext db)
	{
		if (!db.Database.IsSqlite())
		{
			return null;
		}

		var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
		if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
			dataSource.Contains('\n') || dataSource.Contains('\r'))
		{
			return null;
		}

		// Microsoft.Data.Sqlite opens relative paths from the working directory.
		return Path.GetFullPath(dataSource, Directory.GetCurrentDirectory());
	}

	private bool IsDatabaseSqlite()
	{
		if (_databaseIsSqlite is bool known)
		{
			return known;
		}

		try
		{
			using var scope = _scopeFactory.CreateScope();
			_databaseIsSqlite = GetSqlitePath(scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>()) is not null;
		}
		catch
		{
			_databaseIsSqlite = false;
		}

		return _databaseIsSqlite.Value;
	}

	private void SetStage(AppUpdateStage stage, string message, int? progress = null)
	{
		lock (_sync)
		{
			// A late progress report must not undo a later step, a cancellation or a failure.
			if (_stage is AppUpdateStage.Idle or AppUpdateStage.Failed || (_stage > stage && stage == AppUpdateStage.Downloading))
			{
				return;
			}

			if (_updateCancellation?.IsCancellationRequested == true && stage != AppUpdateStage.Restarting)
			{
				return;
			}

			_stage = stage;
			_stageMessage = message;
			_progressPercent = progress;
		}
	}

	private bool IsBusyLocked() =>
		_stage is AppUpdateStage.Downloading or AppUpdateStage.Verifying or AppUpdateStage.WaitingForJobs or AppUpdateStage.Restarting;

	private string? GetInstallRefusalLocked()
	{
		if (_layout is null)
		{
			return _installUnavailableReason;
		}

		if (_latestRelease is null || _latestVersion is null)
		{
			return _lastCheckedAtUtc is null ? "Check for updates first." : "There's no release to install yet.";
		}

		if (!IsUpdateAvailableLocked())
		{
			return $"{AppVersionLabel(_currentVersion)} is already the newest release.";
		}

		if (File.Exists(_layout.PendingFile) || File.Exists(_layout.TrialFile))
		{
			return "An update is already being applied. Restart the service if it is stuck.";
		}

		if (string.Equals(_latestVersion.ToString(), InstallLayout.GetRunningVersionName(_appBaseDirectory), StringComparison.Ordinal))
		{
			return $"{_latestVersion} is already running.";
		}

		return null;
	}

	private bool IsUpdateAvailableLocked() =>
		_latestVersion is not null && !_currentVersion.IsDevelopmentBuild && _latestVersion > _currentVersion;

	private static string AppVersionLabel(ReleaseVersion version) => version.IsDevelopmentBuild ? "This build" : version.ToString();

	private AppUpdateStatus BuildStatus()
	{
		var isSqlite = IsDatabaseSqlite();
		lock (_sync)
		{
			var status = BuildStatusLocked();
			status.DatabaseBackedUp = isSqlite;
			return status;
		}
	}

	private AppUpdateStatus BuildStatusLocked()
	{
		var release = _latestRelease;
		return new AppUpdateStatus
		{
			CurrentVersion = _currentVersion.ToString(),
			IsReleaseBuild = !_currentVersion.IsDevelopmentBuild,
			Repository = _options.CurrentValue.Repository,
			InstallKind = _layout is null ? AppInstallKind.Manual : AppInstallKind.Managed,
			InstallUnavailableReason = _installUnavailableReason,
			LatestRelease = release is null || _latestVersion is null ? null : new AppReleaseSummary
			{
				Version = _latestVersion.ToString(),
				Tag = release.TagName,
				Name = release.Name,
				Notes = release.Body is { Length: > 4000 } body ? body[..4000] + "…" : release.Body,
				HtmlUrl = release.HtmlUrl,
				PublishedAtUtc = release.PublishedAt,
				IsPrerelease = release.Prerelease
			},
			IsUpdateAvailable = IsUpdateAvailableLocked(),
			LastCheckedAtUtc = _lastCheckedAtUtc,
			CheckMessage = _checkMessage,
			Stage = _stage,
			StageMessage = _stageMessage,
			ProgressPercent = _progressPercent,
			TargetVersion = _targetVersion,
			ServerInstanceId = _serverInstanceId,
			DatabaseBackedUp = _databaseIsSqlite == true,
			LastResult = _lastResult
		};
	}

	public override void Dispose()
	{
		_updateCancellation?.Dispose();
		_checkLock.Dispose();
		base.Dispose();
	}
}
