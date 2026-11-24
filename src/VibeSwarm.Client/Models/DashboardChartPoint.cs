namespace VibeSwarm.Client.Models;

public sealed class DashboardChartPoint
{
	public string Label { get; init; } = string.Empty;
	public double Value { get; init; }

	/// <summary>A second series stacked on top of <see cref="Value"/>, such as failed jobs above completed ones.</summary>
	public double SecondaryValue { get; init; }

	public double Total => Value + SecondaryValue;
	public string ValueLabel { get; init; } = string.Empty;
}
