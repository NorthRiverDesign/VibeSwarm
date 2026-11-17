using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services.Updates;

namespace VibeSwarm.Tests;

public sealed class AppUpdateServiceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"updates-{Guid.NewGuid():N}");
	private readonly string _databasePath;
	private readonly FakeReleaseClient _releases = new();
	private readonly FakeStager _stager = new();
	private readonly FakeQueue _queue = new();
	private readonly FakeLifetime _lifetime = new();
	private readonly ServiceProvider _services;

	public AppUpdateServiceTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "versions", "1.0.0"));
		Directory.CreateDirectory(Path.Combine(_root, "data"));
		File.WriteAllText(Path.Combine(_root, InstallLayout.LauncherFileName), "#!/usr/bin/env bash\n");
		_databasePath = Path.Combine(_root, "data", "app.db");

		var collection = new ServiceCollection();
		collection.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
		collection.AddSingleton<IJobQueueControlService>(_queue);
		_services = collection.BuildServiceProvider();
		using var scope = _services.CreateScope();
		scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Database.EnsureCreated();
	}

	public void Dispose()
	{
		_services.Dispose();
		SqliteConnection.ClearAllPools();
		Environment.ExitCode = 0;
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private string AppDirectory => Path.Combine(_root, "versions", "1.0.0");

	private AppUpdateService CreateService(string? installRoot = "<root>", string currentVersion = "1.0.0") =>
		new(new StaticOptionsMonitor(new UpdateOptions { Repository = "owner/app", InstallRoot = installRoot == "<root>" ? _root : installRoot }),
			_releases, _stager, _services.GetRequiredService<IServiceScopeFactory>(), _lifetime,
			NullLogger<AppUpdateService>.Instance, AppDirectory,
			ReleaseVersion.TryParse(currentVersion, out var version) ? version : null);

	private static async Task<AppUpdateStatus> WaitForStageAsync(AppUpdateService service, AppUpdateStage stage)
	{
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (true)
		{
			var status = await service.GetStatusAsync();
			if (status.Stage == stage || DateTime.UtcNow > deadline)
			{
				return status;
			}

			await Task.Delay(20);
		}
	}

	// The service reports Restarting just before it stops the app, so wait for the stop itself.
	private async Task WaitForStopAsync()
	{
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (!_lifetime.Stopped && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}

		Assert.True(_lifetime.Stopped);
	}

	private async Task<Job> AddJobAsync(JobStatus status)
	{
		using var scope = _services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var project = new Project { Id = Guid.NewGuid(), Name = $"App {Guid.NewGuid():N}", WorkingPath = _root };
		var provider = new Provider { Id = Guid.NewGuid(), Name = $"Claude {Guid.NewGuid():N}", Type = ProviderType.Claude, IsEnabled = true };
		var job = new Job { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "Build it", Status = status };
		db.AddRange(project, provider, job);
		await db.SaveChangesAsync();
		return job;
	}

	private async Task SetJobStatusAsync(Guid jobId, JobStatus status)
	{
		using var scope = _services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
		job.Status = status;
		await db.SaveChangesAsync();
	}

	[Fact]
	public async Task ManualInstall_CanCheckButNotInstall()
	{
		_releases.Release = new GitHubRelease { TagName = "v1.1.0" };
		var service = CreateService(installRoot: null);

		var status = await service.CheckNowAsync();
		Assert.Equal(AppInstallKind.Manual, status.InstallKind);
		Assert.True(status.IsUpdateAvailable);
		Assert.False(status.CanInstall);
		Assert.Contains("install.sh", status.InstallUnavailableReason);

		status = await service.StartUpdateAsync();
		Assert.Equal(AppUpdateStage.Failed, status.Stage);
		Assert.Contains("install.sh", status.StageMessage);
		Assert.Equal(0, _stager.Calls);
		Assert.Equal(AppUpdateStage.Idle, (await service.GetStatusAsync()).Stage);
	}

	[Fact]
	public async Task DevelopmentBuild_IsNeverOfferedAnUpdate()
	{
		_releases.Release = new GitHubRelease { TagName = "v1.1.0" };
		var service = CreateService(currentVersion: "0.0.0-dev");

		var status = await service.CheckNowAsync();

		Assert.False(status.IsReleaseBuild);
		Assert.False(status.IsUpdateAvailable);
		Assert.Equal("1.1.0", status.LatestRelease!.Version);
	}

	[Fact]
	public async Task Check_WithNoReleases_SaysSoWithoutFailing()
	{
		_releases.Message = "No releases have been published yet.";
		var status = await CreateService().CheckNowAsync();

		Assert.Null(status.LatestRelease);
		Assert.False(status.IsUpdateAvailable);
		Assert.Equal(AppUpdateStage.Idle, status.Stage);
		Assert.Equal("No releases have been published yet.", status.CheckMessage);
	}

	[Fact]
	public async Task Update_StagesPausesTheQueueAndHandsOverToTheLauncher()
	{
		_releases.Release = new GitHubRelease { TagName = "v1.1.0" };
		var service = CreateService();
		await service.CheckNowAsync();

		var started = await service.StartUpdateAsync();
		Assert.Equal(AppUpdateStage.Downloading, started.Stage);
		await WaitForStopAsync();
		var status = await service.GetStatusAsync();

		Assert.Equal(AppUpdateStage.Restarting, status.Stage);
		Assert.Equal(InstallLayout.RestartExitCode, Environment.ExitCode);
		Assert.True(_queue.State.IsPaused);
		Assert.StartsWith(AppUpdateService.QueuePauseReasonPrefix, _queue.State.PausedReason);

		var pending = KeyValueFile.Read(Path.Combine(_root, "state", InstallLayout.PendingFileName));
		Assert.Equal("1.1.0", pending["VERSION"]);
		Assert.Equal("1.0.0", pending["FROM"]);
		Assert.Equal(_databasePath, pending["SQLITE_PATH"]);
		Assert.True(status.DatabaseBackedUp);
	}

	[Fact]
	public async Task Update_WaitsForRunningJobsAndAnsweredQuestions()
	{
		_releases.Release = new GitHubRelease { TagName = "v1.1.0" };
		var running = await AddJobAsync(JobStatus.Processing);
		var asking = await AddJobAsync(JobStatus.Paused);
		var service = CreateService();
		await service.CheckNowAsync();

		await service.StartUpdateAsync();
		var status = await WaitForStageAsync(service, AppUpdateStage.WaitingForJobs);
		Assert.Contains("1 running job", status.StageMessage);
		Assert.Contains("1 job waiting for your answer", status.StageMessage);
		Assert.False(File.Exists(Path.Combine(_root, "state", InstallLayout.PendingFileName)));
		Assert.False(_lifetime.Stopped);

		await SetJobStatusAsync(running.Id, JobStatus.Completed);
		await SetJobStatusAsync(asking.Id, JobStatus.Cancelled);
		await WaitForStopAsync();

		Assert.Equal(AppUpdateStage.Restarting, (await service.GetStatusAsync()).Stage);
	}

	[Fact]
	public async Task Cancel_WhileWaiting_RemovesTheStagedReleaseAndResumesTheQueue()
	{
		_releases.Release = new GitHubRelease { TagName = "v1.1.0" };
		await AddJobAsync(JobStatus.Processing);
		var service = CreateService();
		await service.CheckNowAsync();
		await service.StartUpdateAsync();
		await WaitForStageAsync(service, AppUpdateStage.WaitingForJobs);
		Assert.True(Directory.Exists(Path.Combine(_root, "versions", "1.1.0")));

		await service.CancelUpdateAsync();
		var status = await WaitForStageAsync(service, AppUpdateStage.Idle);

		Assert.Equal(AppUpdateStage.Idle, status.Stage);
		Assert.False(Directory.Exists(Path.Combine(_root, "versions", "1.1.0")));
		Assert.True(Directory.Exists(AppDirectory));
		Assert.False(_queue.State.IsPaused);
		Assert.False(_lifetime.Stopped);
	}

	[Fact]
	public async Task Update_LeavesAPauseTheUserSetAlone()
	{
		_releases.Release = new GitHubRelease { TagName = "v1.1.0" };
		await _queue.PauseAsync("Holding for the weekend");
		await AddJobAsync(JobStatus.Processing);
		var service = CreateService();
		await service.CheckNowAsync();
		await service.StartUpdateAsync();
		await WaitForStageAsync(service, AppUpdateStage.WaitingForJobs);

		await service.CancelUpdateAsync();
		await WaitForStageAsync(service, AppUpdateStage.Idle);

		Assert.True(_queue.State.IsPaused);
		Assert.Equal("Holding for the weekend", _queue.State.PausedReason);
	}

	[Fact]
	public async Task Update_ThatFailsToStage_ReportsWhyAndNeverPausesTheQueue()
	{
		_releases.Release = new GitHubRelease { TagName = "v1.1.0" };
		_stager.Failure = new UpdateException("The downloaded package doesn't match its published SHA-256.");
		var service = CreateService();
		await service.CheckNowAsync();

		await service.StartUpdateAsync();
		var status = await WaitForStageAsync(service, AppUpdateStage.Failed);

		Assert.Equal(AppUpdateStage.Failed, status.Stage);
		Assert.Contains("SHA-256", status.StageMessage);
		Assert.False(_queue.State.IsPaused);
		Assert.False(_lifetime.Stopped);
	}

	[Fact]
	public async Task Reconcile_ConfirmsTheTrialRecordsTheResultAndRemovesOldReleases()
	{
		// Running 1.1.0, which the launcher switched to from 1.0.0; 0.9.0 is older still.
		var running = Path.Combine(_root, "versions", "1.1.0");
		Directory.CreateDirectory(running);
		Directory.CreateDirectory(Path.Combine(_root, "versions", "0.9.0"));
		Directory.CreateDirectory(Path.Combine(_root, "versions", ".staging-1.2.0-abc"));
		KeyValueFile.Write(Path.Combine(_root, "state", InstallLayout.TrialFileName),
			[new("VERSION", "1.1.0"), new("PREVIOUS", "1.0.0"), new("STARTS", "1")]);
		await _queue.PauseAsync($"{AppUpdateService.QueuePauseReasonPrefix} to 1.1.0");

		var service = new AppUpdateService(
			new StaticOptionsMonitor(new UpdateOptions { InstallRoot = _root }), _releases, _stager,
			_services.GetRequiredService<IServiceScopeFactory>(), _lifetime, NullLogger<AppUpdateService>.Instance,
			running, new ReleaseVersion(1, 1, 0, null));
		await service.ReconcileAfterStartAsync(CancellationToken.None);

		Assert.False(File.Exists(Path.Combine(_root, "state", InstallLayout.TrialFileName)));
		var result = (await service.GetStatusAsync()).LastResult!;
		Assert.True(result.Succeeded);
		Assert.Equal("1.0.0", result.FromVersion);
		Assert.Equal("1.1.0", result.ToVersion);
		Assert.Equal(["1.0.0", "1.1.0"], Directory.EnumerateDirectories(Path.Combine(_root, "versions")).Select(Path.GetFileName).Order());
		Assert.False(_queue.State.IsPaused);
	}

	[Fact]
	public async Task Reconcile_AfterARollback_ShowsItAndResumesTheQueue()
	{
		KeyValueFile.Write(Path.Combine(_root, "state", InstallLayout.LastUpdateFileName),
			[new("RESULT", "rolled-back"), new("FROM", "1.0.0"), new("TO", "1.1.0"), new("MESSAGE", "1.1.0 didn't start."), new("AT", "1794886897")]);
		await _queue.PauseAsync($"{AppUpdateService.QueuePauseReasonPrefix} to 1.1.0");
		var service = CreateService();

		await service.ReconcileAfterStartAsync(CancellationToken.None);

		var result = (await service.GetStatusAsync()).LastResult!;
		Assert.False(result.Succeeded);
		Assert.Equal("1.0.0", result.FromVersion);
		Assert.False(_queue.State.IsPaused);
	}

	[Fact]
	public void InstallLayout_RequiresTheLauncherAndAVersionFolder()
	{
		Assert.Null(InstallLayout.TryResolve(_root, Path.Combine(_root, "elsewhere"), out var reason));
		Assert.Contains("versions", reason);

		File.Delete(Path.Combine(_root, InstallLayout.LauncherFileName));
		Assert.Null(InstallLayout.TryResolve(_root, AppDirectory, out reason));
		Assert.Contains("launcher", reason);
	}

	[Fact]
	public void KeyValueFile_RefusesValuesThatWouldBreakALine()
	{
		Assert.Throws<ArgumentException>(() =>
			KeyValueFile.Write(Path.Combine(_root, "state", "x.env"), [new("VERSION", "1.0.0\nSQLITE_PATH=/etc/passwd")]));
	}

	private sealed class StaticOptionsMonitor(UpdateOptions value) : IOptionsMonitor<UpdateOptions>
	{
		public UpdateOptions CurrentValue => value;
		public UpdateOptions Get(string? name) => value;
		public IDisposable? OnChange(Action<UpdateOptions, string?> listener) => null;
	}

	private sealed class FakeReleaseClient : IGitHubReleaseClient
	{
		public GitHubRelease? Release { get; set; }
		public string? Message { get; set; }

		public Task<ReleaseLookup> GetLatestReleaseAsync(string repository, bool includePrereleases, CancellationToken cancellationToken) =>
			Task.FromResult(new ReleaseLookup(Release, Message));
	}

	private sealed class FakeStager : IReleasePackageStager
	{
		public int Calls { get; private set; }
		public Exception? Failure { get; set; }

		public Task<string> StageAsync(GitHubRelease release, ReleaseVersion version, InstallLayout layout,
			IProgress<int>? downloadProgress, CancellationToken cancellationToken)
		{
			Calls++;
			if (Failure is not null)
			{
				throw Failure;
			}

			var directory = layout.GetVersionDirectory(version.ToString());
			Directory.CreateDirectory(directory);
			return Task.FromResult(directory);
		}
	}

	private sealed class FakeQueue : IJobQueueControlService
	{
		public JobQueueState State { get; } = new();

		public Task<JobQueueState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

		public Task<JobQueueState> PauseAsync(string? reason = null, bool cancelRunningJobs = false, CancellationToken cancellationToken = default)
		{
			State.IsPaused = true;
			State.PausedReason = reason;
			return Task.FromResult(State);
		}

		public Task<JobQueueState> ResumeAsync(CancellationToken cancellationToken = default)
		{
			State.IsPaused = false;
			State.PausedReason = null;
			return Task.FromResult(State);
		}

		public Task<ProjectQueueState> GetProjectStateAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProjectQueueState> PauseProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<ProjectQueueState> ResumeProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FakeLifetime : IHostApplicationLifetime
	{
		private readonly CancellationTokenSource _started = new();

		public FakeLifetime() => _started.Cancel();

		private volatile bool _stopped;

		public bool Stopped => _stopped;
		public CancellationToken ApplicationStarted => _started.Token;
		public CancellationToken ApplicationStopping => CancellationToken.None;
		public CancellationToken ApplicationStopped => CancellationToken.None;
		public void StopApplication() => _stopped = true;
	}
}
