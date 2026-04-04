using Microsoft.Extensions.Options;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public class DeveloperUpdateService : IDeveloperModeService
{
	private const string DefaultBuildCommand = "dotnet build VibeSwarm.sln --nologo";

	private readonly IOptionsMonitor<DeveloperModeOptions> _optionsMonitor;
	private readonly ISystemCommandRunner _commandRunner;
	private readonly IJobUpdateService _jobUpdateService;
	private readonly ILogger<DeveloperUpdateService> _logger;
	private readonly object _sync = new();
	private DeveloperModeStatus _status;

	public DeveloperUpdateService(
		IOptionsMonitor<DeveloperModeOptions> optionsMonitor,
		ISystemCommandRunner commandRunner,
		IJobUpdateService jobUpdateService,
		ILogger<DeveloperUpdateService> logger)
	{
		_optionsMonitor = optionsMonitor;
		_commandRunner = commandRunner;
		_jobUpdateService = jobUpdateService;
		_logger = logger;
		_status = CreateConfiguredStatus(GetResolvedOptions());
	}

	public Task<DeveloperModeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			RefreshIdleStatusLocked();
			return Task.FromResult(CloneStatus(_status));
		}
	}

	public async Task<DeveloperModeStatus> StartSelfUpdateAsync(CancellationToken cancellationToken = default)
	{
		DeveloperModeStatus startedStatus;
		ResolvedDeveloperModeOptions options;

		lock (_sync)
		{
			options = GetResolvedOptions();
			RefreshIdleStatusLocked(options);

			if (!options.Enabled)
			{
				throw new InvalidOperationException("Developer mode is disabled.");
			}

			if (!_status.SupportsSelfUpdate)
			{
				throw new InvalidOperationException("Developer mode is enabled, but the rebuild/restart commands are not fully configured.");
			}

			if (_status.IsUpdateInProgress)
			{
				return CloneStatus(_status);
			}

			var timestamp = DateTime.UtcNow;
			_status = CreateConfiguredStatus(options);
			_status.IsUpdateInProgress = true;
			_status.Stage = DeveloperUpdateStage.Building;
			_status.StatusMessage = "Rebuilding the application...";
			_status.StartedAtUtc = timestamp;
			_status.LastUpdatedAtUtc = timestamp;
			startedStatus = CloneStatus(_status);
		}

		await _jobUpdateService.NotifyDeveloperUpdateStatusChanged(startedStatus);
		_ = Task.Run(() => ExecuteUpdateAsync(options), CancellationToken.None);

		return startedStatus;
	}

	private async Task ExecuteUpdateAsync(ResolvedDeveloperModeOptions options)
	{
		try
		{
			await AppendOutputAsync($"Running build command in {options.WorkingDirectory}", isError: false);

			var buildResult = await _commandRunner.RunAsync(
				options.BuildCommand!,
				options.WorkingDirectory,
				HandleCommandOutputAsync,
				CancellationToken.None);

			if (!buildResult.Success)
			{
				await FailAsync(buildResult.ErrorMessage ?? $"Build failed with exit code {buildResult.ExitCode}.");
				return;
			}

			await UpdateStatusAsync(status =>
			{
				status.Stage = DeveloperUpdateStage.Restarting;
				status.StatusMessage = "Build succeeded. Restarting the service...";
			});

			await AppendOutputAsync("Build succeeded. Launching restart command...", isError: false);

			var restartResult = await _commandRunner.LaunchDetachedAsync(
				options.RestartCommand!,
				options.WorkingDirectory,
				options.RestartDelaySeconds,
				CancellationToken.None);

			if (!restartResult.Success)
			{
				await FailAsync(restartResult.ErrorMessage ?? "Failed to launch the restart command.");
				return;
			}

			await AppendOutputAsync("Restart command launched. Waiting for the app to come back online...", isError: false);
			await UpdateStatusAsync(status =>
			{
				status.Stage = DeveloperUpdateStage.Restarting;
				status.StatusMessage = "Restart command launched. Waiting for the app to come back online...";
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Developer self-update failed unexpectedly.");
			await FailAsync(ex.Message);
		}
	}

	private Task HandleCommandOutputAsync(string line, bool isError)
	{
		return AppendOutputAsync(line, isError);
	}

	private async Task AppendOutputAsync(string line, bool isError)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}

		DeveloperUpdateOutputLine outputLine;

		lock (_sync)
		{
			var options = GetResolvedOptions();
			outputLine = new DeveloperUpdateOutputLine
			{
				TimestampUtc = DateTime.UtcNow,
				Text = line.TrimEnd(),
				IsError = isError
			};

			_status.LastUpdatedAtUtc = outputLine.TimestampUtc;
			_status.RecentOutput.Add(outputLine);
			while (_status.RecentOutput.Count > options.MaxOutputLines)
			{
				_status.RecentOutput.RemoveAt(0);
			}
		}

		await _jobUpdateService.NotifyDeveloperUpdateOutputAdded(outputLine);
	}

	private async Task UpdateStatusAsync(Action<DeveloperModeStatus> update)
	{
		DeveloperModeStatus snapshot;

		lock (_sync)
		{
			update(_status);
			_status.LastUpdatedAtUtc = DateTime.UtcNow;
			snapshot = CloneStatus(_status);
		}

		await _jobUpdateService.NotifyDeveloperUpdateStatusChanged(snapshot);
	}

	private async Task FailAsync(string message)
	{
		_logger.LogWarning("Developer self-update failed: {Message}", message);
		await AppendOutputAsync(message, isError: true);
		await UpdateStatusAsync(status =>
		{
			status.IsUpdateInProgress = false;
			status.Stage = DeveloperUpdateStage.Failed;
			status.StatusMessage = message;
		});
	}

	private void RefreshIdleStatusLocked()
	{
		RefreshIdleStatusLocked(GetResolvedOptions());
	}

	private void RefreshIdleStatusLocked(ResolvedDeveloperModeOptions options)
	{
		if (_status.IsUpdateInProgress)
		{
			_status.IsEnabled = options.Enabled;
			_status.BuildCommandSummary = options.BuildCommand;
			_status.RestartCommandSummary = options.RestartCommand;
			_status.WorkingDirectory = options.WorkingDirectory;
			return;
		}

		if (_status.Stage == DeveloperUpdateStage.Failed && options.Enabled)
		{
			_status.IsEnabled = true;
			_status.BuildCommandSummary = options.BuildCommand;
			_status.RestartCommandSummary = options.RestartCommand;
			_status.WorkingDirectory = options.WorkingDirectory;
			return;
		}

		_status = CreateConfiguredStatus(options);
	}

	private ResolvedDeveloperModeOptions GetResolvedOptions()
	{
		var options = _optionsMonitor.CurrentValue;
		var workingDirectory = ResolveWorkingDirectory(options.WorkingDirectory);
		var buildCommand = string.IsNullOrWhiteSpace(options.BuildCommand)
			? ResolveDefaultBuildCommand(workingDirectory)
			: options.BuildCommand.Trim();
		var restartCommand = string.IsNullOrWhiteSpace(options.RestartCommand)
			? ResolveRestartCommand(options.ServiceName)
			: options.RestartCommand.Trim();

		return new ResolvedDeveloperModeOptions
		{
			Enabled = options.Enabled,
			BuildCommand = buildCommand,
			RestartCommand = restartCommand,
			WorkingDirectory = workingDirectory,
			RestartDelaySeconds = Math.Max(0, options.RestartDelaySeconds),
			MaxOutputLines = Math.Clamp(options.MaxOutputLines, 20, 500)
		};
	}

	private static string ResolveWorkingDirectory(string? configuredWorkingDirectory)
	{
		if (!string.IsNullOrWhiteSpace(configuredWorkingDirectory))
		{
			return configuredWorkingDirectory.Trim();
		}

		var searchRoots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
		foreach (var root in searchRoots)
		{
			var directory = new DirectoryInfo(root);
			while (directory is not null)
			{
				if (File.Exists(Path.Combine(directory.FullName, "VibeSwarm.sln")))
				{
					return directory.FullName;
				}

				directory = directory.Parent;
			}
		}

		return Directory.GetCurrentDirectory();
	}

	private static string? ResolveDefaultBuildCommand(string workingDirectory)
	{
		return File.Exists(Path.Combine(workingDirectory, "VibeSwarm.sln"))
			? DefaultBuildCommand
			: null;
	}

	private static string? ResolveRestartCommand(string? serviceName)
	{
		if (string.IsNullOrWhiteSpace(serviceName))
		{
			return null;
		}

		return OperatingSystem.IsWindows()
			? null
			: $"systemctl restart {serviceName.Trim()}";
	}

	private static DeveloperModeStatus CreateConfiguredStatus(ResolvedDeveloperModeOptions options)
	{
		if (!options.Enabled)
		{
			return new DeveloperModeStatus
			{
				IsEnabled = false,
				Stage = DeveloperUpdateStage.Disabled,
				StatusMessage = "Developer mode is disabled."
			};
		}

		if (string.IsNullOrWhiteSpace(options.BuildCommand) || string.IsNullOrWhiteSpace(options.RestartCommand))
		{
			return new DeveloperModeStatus
			{
				IsEnabled = true,
				Stage = DeveloperUpdateStage.Ready,
				StatusMessage = "Developer mode is enabled, but self-update needs a build command and a restart command.",
				BuildCommandSummary = options.BuildCommand,
				RestartCommandSummary = options.RestartCommand,
				WorkingDirectory = options.WorkingDirectory
			};
		}

		return new DeveloperModeStatus
		{
			IsEnabled = true,
			Stage = DeveloperUpdateStage.Ready,
			StatusMessage = "Developer mode is enabled. Rebuild and restart is ready.",
			BuildCommandSummary = options.BuildCommand,
			RestartCommandSummary = options.RestartCommand,
			WorkingDirectory = options.WorkingDirectory
		};
	}

	private static DeveloperModeStatus CloneStatus(DeveloperModeStatus status)
	{
		return new DeveloperModeStatus
		{
			IsEnabled = status.IsEnabled,
			IsUpdateInProgress = status.IsUpdateInProgress,
			Stage = status.Stage,
			StatusMessage = status.StatusMessage,
			BuildCommandSummary = status.BuildCommandSummary,
			RestartCommandSummary = status.RestartCommandSummary,
			WorkingDirectory = status.WorkingDirectory,
			StartedAtUtc = status.StartedAtUtc,
			LastUpdatedAtUtc = status.LastUpdatedAtUtc,
			RecentOutput = status.RecentOutput
				.Select(line => new DeveloperUpdateOutputLine
				{
					TimestampUtc = line.TimestampUtc,
					Text = line.Text,
					IsError = line.IsError
				})
				.ToList()
		};
	}

	private sealed class ResolvedDeveloperModeOptions
	{
		public bool Enabled { get; init; }
		public string? BuildCommand { get; init; }
		public string? RestartCommand { get; init; }
		public string WorkingDirectory { get; init; } = string.Empty;
		public int RestartDelaySeconds { get; init; }
		public int MaxOutputLines { get; init; }
	}
}
