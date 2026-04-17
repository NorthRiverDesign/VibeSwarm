namespace VibeSwarm.Client.Models;

public sealed class ProjectAgentModelSelectionChange
{
	public Guid AgentId { get; set; }

	public string? PreferredModelId { get; set; }

	public string? PreferredReasoningEffort { get; set; }
}
