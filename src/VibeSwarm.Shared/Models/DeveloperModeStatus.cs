namespace VibeSwarm.Shared.Models;

public class DeveloperModeStatus
{
	public bool IsEnabled { get; set; }
	public bool IsUpdateInProgress { get; set; }
	public DeveloperUpdateStage Stage { get; set; } = DeveloperUpdateStage.Disabled;
	public string StatusMessage { get; set; } = string.Empty;
	public string? BuildCommandSummary { get; set; }
	public string? RestartCommandSummary { get; set; }
	public string? WorkingDirectory { get; set; }
	public DateTime? StartedAtUtc { get; set; }
	public DateTime? LastUpdatedAtUtc { get; set; }
	public List<DeveloperUpdateOutputLine> RecentOutput { get; set; } = [];

	public bool SupportsSelfUpdate =>
		!string.IsNullOrWhiteSpace(BuildCommandSummary) &&
		!string.IsNullOrWhiteSpace(RestartCommandSummary);

	public bool CanStartUpdate => IsEnabled && SupportsSelfUpdate && !IsUpdateInProgress;
}

public class DeveloperUpdateOutputLine
{
	public DateTime TimestampUtc { get; set; }
	public string Text { get; set; } = string.Empty;
	public bool IsError { get; set; }
}

public enum DeveloperUpdateStage
{
	Disabled,
	Ready,
	Building,
	Restarting,
	Failed
}
