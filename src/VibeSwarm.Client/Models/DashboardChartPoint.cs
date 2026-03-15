namespace VibeSwarm.Client.Models;

public sealed class DashboardChartPoint
{
	public string Label { get; init; } = string.Empty;
	public double Value { get; init; }
	public string ValueLabel { get; init; } = string.Empty;
}
