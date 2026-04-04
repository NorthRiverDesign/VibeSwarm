using VibeSwarm.Shared.Models;

namespace VibeSwarm.Client.Services;

public class DeveloperUpdateOverlayService
{
	private const int MaxOutputLines = 200;

	public event Action? OnChange;

	public DeveloperModeStatus? CurrentStatus { get; private set; }

	public bool IsVisible =>
		CurrentStatus?.IsUpdateInProgress == true ||
		CurrentStatus?.Stage == DeveloperUpdateStage.Failed;

	public void SetStatus(DeveloperModeStatus? status)
	{
		CurrentStatus = status is null ? null : CloneStatus(status);
		OnChange?.Invoke();
	}

	public void AppendOutput(DeveloperUpdateOutputLine line)
	{
		if (CurrentStatus is null)
		{
			return;
		}

		CurrentStatus.RecentOutput.Add(new DeveloperUpdateOutputLine
		{
			TimestampUtc = line.TimestampUtc,
			Text = line.Text,
			IsError = line.IsError
		});

		while (CurrentStatus.RecentOutput.Count > MaxOutputLines)
		{
			CurrentStatus.RecentOutput.RemoveAt(0);
		}

		CurrentStatus.LastUpdatedAtUtc = line.TimestampUtc;
		OnChange?.Invoke();
	}

	public void Clear()
	{
		CurrentStatus = null;
		OnChange?.Invoke();
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
}
