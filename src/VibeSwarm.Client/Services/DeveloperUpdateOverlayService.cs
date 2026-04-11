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
		if (status is null)
		{
			CurrentStatus = null;
			OnChange?.Invoke();
			return;
		}

		var nextStatus = CloneStatus(status);
		if (CurrentStatus is not null)
		{
			nextStatus = MergeStatus(CurrentStatus, nextStatus);
		}

		CurrentStatus = nextStatus;
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
			ServerInstanceId = status.ServerInstanceId,
			BuildCommandSummary = status.BuildCommandSummary,
			RestartCommandSummary = status.RestartCommandSummary,
			WorkingDirectory = status.WorkingDirectory,
			StartedAtUtc = status.StartedAtUtc,
			LastUpdatedAtUtc = status.LastUpdatedAtUtc,
			RestartDeadlineUtc = status.RestartDeadlineUtc,
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

	private static DeveloperModeStatus MergeStatus(DeveloperModeStatus currentStatus, DeveloperModeStatus nextStatus)
	{
		if (!nextStatus.IsUpdateInProgress && nextStatus.Stage != DeveloperUpdateStage.Failed)
		{
			return nextStatus;
		}

		if (nextStatus.StartedAtUtc is null)
		{
			nextStatus.StartedAtUtc = currentStatus.StartedAtUtc;
		}

		if (string.IsNullOrWhiteSpace(nextStatus.WorkingDirectory))
		{
			nextStatus.WorkingDirectory = currentStatus.WorkingDirectory;
		}

		if (string.IsNullOrWhiteSpace(nextStatus.ServerInstanceId))
		{
			nextStatus.ServerInstanceId = currentStatus.ServerInstanceId;
		}

		if (string.IsNullOrWhiteSpace(nextStatus.BuildCommandSummary))
		{
			nextStatus.BuildCommandSummary = currentStatus.BuildCommandSummary;
		}

		if (string.IsNullOrWhiteSpace(nextStatus.RestartCommandSummary))
		{
			nextStatus.RestartCommandSummary = currentStatus.RestartCommandSummary;
		}

		if (nextStatus.RestartDeadlineUtc is null)
		{
			nextStatus.RestartDeadlineUtc = currentStatus.RestartDeadlineUtc;
		}

		if (currentStatus.RecentOutput.Count == 0)
		{
			return nextStatus;
		}

		var mergedOutput = new List<DeveloperUpdateOutputLine>(currentStatus.RecentOutput.Count + nextStatus.RecentOutput.Count);
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var line in currentStatus.RecentOutput.Concat(nextStatus.RecentOutput))
		{
			var key = $"{line.TimestampUtc.Ticks}|{line.IsError}|{line.Text}";
			if (!seen.Add(key))
			{
				continue;
			}

			mergedOutput.Add(new DeveloperUpdateOutputLine
			{
				TimestampUtc = line.TimestampUtc,
				Text = line.Text,
				IsError = line.IsError
			});
		}

		nextStatus.RecentOutput = mergedOutput
			.OrderBy(line => line.TimestampUtc)
			.TakeLast(MaxOutputLines)
			.ToList();

		if (currentStatus.LastUpdatedAtUtc is DateTime currentLastUpdatedAtUtc &&
			(nextStatus.LastUpdatedAtUtc is null || currentLastUpdatedAtUtc > nextStatus.LastUpdatedAtUtc))
		{
			nextStatus.LastUpdatedAtUtc = currentLastUpdatedAtUtc;
		}

		return nextStatus;
	}
}
