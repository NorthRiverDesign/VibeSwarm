namespace VibeSwarm.Shared.Models;

public sealed class JobProjectCountSummary
{
	public Guid ProjectId { get; set; }

	public int TotalCount { get; set; }

	public int ActiveCount { get; set; }
}
