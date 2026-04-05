using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class DeveloperUpdateServiceTests
{
	[Fact]
	public async Task StartSelfUpdateAsync_BuildFailureBroadcastsFailureButSubsequentStatusReturnsReady()
	{
		var commandRunner = new FakeSystemCommandRunner
		{
			BuildResult = new CommandExecutionResult(false, 1, "dotnet build failed")
		};
		var updates = new TestJobUpdateService();
		var service = CreateService(commandRunner, updates, new DeveloperModeOptions
		{
			Enabled = true,
			BuildCommand = "dotnet build",
			RestartCommand = "systemctl restart vibeswarm.service",
			WorkingDirectory = "/tmp"
		});

		var started = await service.StartSelfUpdateAsync();
		await WaitForAsync(() => Task.FromResult(updates.StatusUpdates.Any(update => update.Stage == DeveloperUpdateStage.Failed)));
		var ready = await service.GetStatusAsync();

		Assert.True(started.IsUpdateInProgress);
		Assert.Equal(DeveloperUpdateStage.Building, started.Stage);
		Assert.Equal(DeveloperUpdateStage.Ready, ready.Stage);
		Assert.False(ready.IsUpdateInProgress);
		Assert.Empty(commandRunner.DetachedCommands);
		Assert.Contains("dotnet build failed", updates.StatusUpdates.Single(update => update.Stage == DeveloperUpdateStage.Failed).StatusMessage);
		Assert.Equal("dotnet build", ready.BuildCommandSummary);
		Assert.Equal("systemctl restart vibeswarm.service", ready.RestartCommandSummary);
	}

	[Fact]
	public async Task StartSelfUpdateAsync_SuccessLaunchesDetachedRestartAndLeavesRestartingState()
	{
		var commandRunner = new FakeSystemCommandRunner();
		commandRunner.BuildOutput.Add(("Determining projects to restore...", false));
		commandRunner.BuildOutput.Add(("Build succeeded.", false));
		var updates = new TestJobUpdateService();
		var service = CreateService(commandRunner, updates, new DeveloperModeOptions
		{
			Enabled = true,
			BuildCommand = "dotnet build",
			ServiceName = "vibeswarm.service",
			WorkingDirectory = "/tmp",
			RestartDelaySeconds = 1
		});

		await service.StartSelfUpdateAsync();
		await WaitForAsync(async () => (await service.GetStatusAsync()).Stage == DeveloperUpdateStage.Restarting);
		var restarting = await service.GetStatusAsync();

		Assert.Equal(DeveloperUpdateStage.Restarting, restarting.Stage);
		Assert.True(restarting.IsUpdateInProgress);
		Assert.False(string.IsNullOrWhiteSpace(restarting.ServerInstanceId));
		Assert.NotNull(restarting.RestartDeadlineUtc);
		Assert.Single(commandRunner.DetachedCommands);
		Assert.Contains("systemctl restart vibeswarm.service", commandRunner.DetachedCommands[0].Command);
		Assert.Contains("Build succeeded.", restarting.RecentOutput.Select(line => line.Text));
		Assert.Contains(updates.StatusUpdates, update => update.Stage == DeveloperUpdateStage.Restarting);
		Assert.True(updates.OutputLines.Count >= 2);
	}

	[Fact]
	public async Task GetStatusAsync_UsesDefaultBuildCommandWhenSolutionExists()
	{
		var commandRunner = new FakeSystemCommandRunner();
		var updates = new TestJobUpdateService();
		var service = CreateService(commandRunner, updates, new DeveloperModeOptions
		{
			Enabled = true,
			ServiceName = "vibeswarm.service"
		});

		var status = await service.GetStatusAsync();

		Assert.True(status.IsEnabled);
		Assert.Equal("dotnet build VibeSwarm.sln --nologo", status.BuildCommandSummary);
		Assert.Equal("systemctl restart vibeswarm.service", status.RestartCommandSummary);
		Assert.True(status.CanStartUpdate);
	}

	[Fact]
	public async Task GetStatusAsync_DuringBuildIncludesLiveOutput()
	{
		var commandRunner = new FakeSystemCommandRunner
		{
			BuildCompletionSource = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously)
		};
		commandRunner.BuildOutput.Add(("Determining projects to restore...", false));
		var updates = new TestJobUpdateService();
		var service = CreateService(commandRunner, updates, new DeveloperModeOptions
		{
			Enabled = true,
			BuildCommand = "dotnet build",
			RestartCommand = "systemctl restart vibeswarm.service",
			WorkingDirectory = "/tmp"
		});

		await service.StartSelfUpdateAsync();
		await WaitForAsync(async () =>
		{
			var status = await service.GetStatusAsync();
			return status.Stage == DeveloperUpdateStage.Building &&
				status.RecentOutput.Any(line => line.Text == "Determining projects to restore...");
		});

		var building = await service.GetStatusAsync();

		Assert.True(building.IsUpdateInProgress);
		Assert.Equal(DeveloperUpdateStage.Building, building.Stage);
		Assert.Contains(building.RecentOutput, line => line.Text == "Determining projects to restore...");

		commandRunner.BuildCompletionSource.SetResult(new CommandExecutionResult(true, 0));
		await WaitForAsync(async () => (await service.GetStatusAsync()).Stage == DeveloperUpdateStage.Restarting);
	}

	[Fact]
	public async Task GetStatusAsync_WhenRestartTimeoutExpires_ReturnsFailureOnceThenResetsToReady()
	{
		var commandRunner = new FakeSystemCommandRunner();
		var updates = new TestJobUpdateService();
		var service = CreateService(commandRunner, updates, new DeveloperModeOptions
		{
			Enabled = true,
			BuildCommand = "dotnet build",
			RestartCommand = "systemctl restart vibeswarm.service",
			WorkingDirectory = "/tmp",
			RestartTimeoutSeconds = 0
		});

		await service.StartSelfUpdateAsync();
		await WaitForAsync(() => Task.FromResult(updates.StatusUpdates.Any(update => update.Stage == DeveloperUpdateStage.Restarting)));
		var failed = await service.GetStatusAsync();
		var ready = await service.GetStatusAsync();

		Assert.Equal(DeveloperUpdateStage.Failed, failed.Stage);
		Assert.False(failed.IsUpdateInProgress);
		Assert.Null(failed.RestartDeadlineUtc);
		Assert.Contains("still running", failed.StatusMessage);
		Assert.Contains(updates.OutputLines, line => line.IsError && line.Text.Contains("still running", StringComparison.Ordinal));
		Assert.Contains(updates.StatusUpdates, update => update.Stage == DeveloperUpdateStage.Failed && update.StatusMessage.Contains("still running", StringComparison.Ordinal));
		Assert.Equal(DeveloperUpdateStage.Ready, ready.Stage);
		Assert.False(ready.IsUpdateInProgress);
	}

	private static DeveloperUpdateService CreateService(
		FakeSystemCommandRunner commandRunner,
		TestJobUpdateService updates,
		DeveloperModeOptions options)
	{
		return new DeveloperUpdateService(
			new FakeOptionsMonitor(options),
			commandRunner,
			updates,
			NullLogger<DeveloperUpdateService>.Instance);
	}

	private static async Task WaitForAsync(Func<Task<bool>> predicate)
	{
		for (var attempt = 0; attempt < 50; attempt++)
		{
			if (await predicate())
			{
				return;
			}

			await Task.Delay(20);
		}

		Assert.Fail("Timed out waiting for the developer update state to change.");
	}

	private sealed class FakeOptionsMonitor : IOptionsMonitor<DeveloperModeOptions>
	{
		public FakeOptionsMonitor(DeveloperModeOptions currentValue)
		{
			CurrentValue = currentValue;
		}

		public DeveloperModeOptions CurrentValue { get; }

		public DeveloperModeOptions Get(string? name) => CurrentValue;

		public IDisposable? OnChange(Action<DeveloperModeOptions, string?> listener) => null;
	}

	private sealed class FakeSystemCommandRunner : ISystemCommandRunner
	{
		public CommandExecutionResult BuildResult { get; set; } = new(true, 0);
		public TaskCompletionSource<CommandExecutionResult>? BuildCompletionSource { get; set; }
		public List<(string Text, bool IsError)> BuildOutput { get; } = [];
		public List<(string Command, string WorkingDirectory, int DelaySeconds)> DetachedCommands { get; } = [];

		public async Task<CommandExecutionResult> RunAsync(string command, string workingDirectory, Func<string, bool, Task>? onOutput = null, CancellationToken cancellationToken = default)
		{
			foreach (var (text, isError) in BuildOutput)
			{
				if (onOutput is not null)
				{
					await onOutput(text, isError);
				}
			}

			if (BuildCompletionSource is not null)
			{
				return await BuildCompletionSource.Task;
			}

			return BuildResult;
		}

		public Task<CommandLaunchResult> LaunchDetachedAsync(string command, string workingDirectory, int delaySeconds = 0, CancellationToken cancellationToken = default)
		{
			DetachedCommands.Add((command, workingDirectory, delaySeconds));
			return Task.FromResult(new CommandLaunchResult(true));
		}
	}

	private sealed class TestJobUpdateService : IJobUpdateService
	{
		public List<DeveloperModeStatus> StatusUpdates { get; } = [];
		public List<DeveloperUpdateOutputLine> OutputLines { get; } = [];

		public Task NotifyJobStatusChanged(Guid jobId, string status) => Task.CompletedTask;
		public Task NotifyJobActivity(Guid jobId, string activity, DateTime timestamp) => Task.CompletedTask;
		public Task NotifyJobMessageAdded(Guid jobId) => Task.CompletedTask;
		public Task NotifyJobCompleted(Guid jobId, bool success, string? errorMessage = null) => Task.CompletedTask;
		public Task NotifyJobListChanged() => Task.CompletedTask;
		public Task NotifyJobCreated(Guid jobId, Guid projectId) => Task.CompletedTask;
		public Task NotifyJobDeleted(Guid jobId, Guid projectId) => Task.CompletedTask;
		public Task NotifyJobHeartbeat(Guid jobId, DateTime timestamp) => Task.CompletedTask;
		public Task NotifyJobOutput(Guid jobId, string line, bool isError, DateTime timestamp) => Task.CompletedTask;
		public Task NotifyProcessStarted(Guid jobId, int processId, string command) => Task.CompletedTask;
		public Task NotifyProcessExited(Guid jobId, int processId, int exitCode, TimeSpan duration) => Task.CompletedTask;
		public Task NotifyJobGitDiffUpdated(Guid jobId, bool hasChanges) => Task.CompletedTask;
		public Task NotifyJobInteractionRequired(Guid jobId, string prompt, string interactionType, List<string>? choices = null, string? defaultResponse = null) => Task.CompletedTask;
		public Task NotifyJobResumed(Guid jobId) => Task.CompletedTask;
		public Task NotifyJobCycleProgress(Guid jobId, int currentCycle, int maxCycles) => Task.CompletedTask;
		public Task NotifyIdeaStarted(Guid ideaId, Guid projectId, Guid jobId) => Task.CompletedTask;
		public Task NotifyIdeasProcessingStateChanged(Guid projectId, bool isActive) => Task.CompletedTask;
		public Task NotifyIdeaCreated(Guid ideaId, Guid projectId) => Task.CompletedTask;
		public Task NotifyIdeaDeleted(Guid ideaId, Guid projectId) => Task.CompletedTask;
		public Task NotifyIdeaUpdated(Guid ideaId, Guid projectId) => Task.CompletedTask;
		public Task NotifyProviderUsageWarning(Guid providerId, string providerName, int percentUsed, string message, bool isExhausted, DateTime? resetTime) => Task.CompletedTask;
		public Task NotifyProviderRateLimited(Guid providerId, string providerName, string message, DateTime? resetTime) => Task.CompletedTask;
		public Task NotifyAutoPilotStateChanged(Guid projectId, Shared.Data.IterationLoop loop) => Task.CompletedTask;

		public Task NotifyDeveloperUpdateStatusChanged(DeveloperModeStatus status)
		{
			StatusUpdates.Add(status);
			return Task.CompletedTask;
		}

		public Task NotifyDeveloperUpdateOutputAdded(DeveloperUpdateOutputLine line)
		{
			OutputLines.Add(line);
			return Task.CompletedTask;
		}
	}
}
