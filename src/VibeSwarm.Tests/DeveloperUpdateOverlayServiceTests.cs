using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Tests;

public sealed class DeveloperUpdateOverlayServiceTests
{
	[Fact]
	public void SetStatus_PreservesExistingOutput_WhenIncomingStatusIsMissingLines()
	{
		var service = new DeveloperUpdateOverlayService();
		var startedAtUtc = DateTime.UtcNow;

		service.SetStatus(new DeveloperModeStatus
		{
			IsEnabled = true,
			IsUpdateInProgress = true,
			Stage = DeveloperUpdateStage.Building,
			StatusMessage = "Building...",
			StartedAtUtc = startedAtUtc
		});

		service.AppendOutput(new DeveloperUpdateOutputLine
		{
			TimestampUtc = startedAtUtc.AddSeconds(1),
			Text = "Determining projects to restore...",
			IsError = false
		});

		service.SetStatus(new DeveloperModeStatus
		{
			IsEnabled = true,
			IsUpdateInProgress = true,
			Stage = DeveloperUpdateStage.Building,
			StatusMessage = "Still building...",
			StartedAtUtc = startedAtUtc,
			RecentOutput = []
		});

		var currentStatus = Assert.IsType<DeveloperModeStatus>(service.CurrentStatus);
		Assert.Single(currentStatus.RecentOutput);
		Assert.Equal("Determining projects to restore...", currentStatus.RecentOutput[0].Text);
	}

	[Fact]
	public void SetStatus_DoesNotCarryOutputIntoReadyState()
	{
		var service = new DeveloperUpdateOverlayService();
		var startedAtUtc = DateTime.UtcNow;

		service.SetStatus(new DeveloperModeStatus
		{
			IsEnabled = true,
			IsUpdateInProgress = true,
			Stage = DeveloperUpdateStage.Building,
			StatusMessage = "Building...",
			StartedAtUtc = startedAtUtc,
			RecentOutput =
			[
				new DeveloperUpdateOutputLine
				{
					TimestampUtc = startedAtUtc.AddSeconds(1),
					Text = "Build succeeded.",
					IsError = false
				}
			]
		});

		service.SetStatus(new DeveloperModeStatus
		{
			IsEnabled = true,
			IsUpdateInProgress = false,
			Stage = DeveloperUpdateStage.Ready,
			StatusMessage = "Developer mode is enabled. Rebuild and restart is ready.",
			RecentOutput = []
		});

		var currentStatus = Assert.IsType<DeveloperModeStatus>(service.CurrentStatus);
		Assert.Empty(currentStatus.RecentOutput);
		Assert.Equal(DeveloperUpdateStage.Ready, currentStatus.Stage);
	}

	[Fact]
	public void SetStatus_PreservesServerInstanceIdAndRestartDeadline_WhenIncomingStatusOmitsThem()
	{
		var service = new DeveloperUpdateOverlayService();
		var restartDeadlineUtc = DateTime.UtcNow.AddSeconds(30);

		service.SetStatus(new DeveloperModeStatus
		{
			IsEnabled = true,
			IsUpdateInProgress = true,
			Stage = DeveloperUpdateStage.Restarting,
			StatusMessage = "Restarting...",
			ServerInstanceId = "instance-a",
			RestartDeadlineUtc = restartDeadlineUtc
		});

		service.SetStatus(new DeveloperModeStatus
		{
			IsEnabled = true,
			IsUpdateInProgress = true,
			Stage = DeveloperUpdateStage.Restarting,
			StatusMessage = "Still restarting..."
		});

		var currentStatus = Assert.IsType<DeveloperModeStatus>(service.CurrentStatus);
		Assert.Equal("instance-a", currentStatus.ServerInstanceId);
		Assert.Equal(restartDeadlineUtc, currentStatus.RestartDeadlineUtc);
	}
}
