namespace VibeSwarm.Shared.Data;

public class JobExecutionTarget
{
	public Guid ProviderId { get; set; }

	public string ProviderName { get; set; } = string.Empty;

	public string? ModelId { get; set; }

	public int Order { get; set; }

	public string Source { get; set; } = string.Empty;
}