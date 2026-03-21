namespace VibeSwarm.Client.Models;

public sealed class ProjectTeamRoleModelSelectionChange
{
	public Guid TeamRoleId { get; set; }

	public string? PreferredModelId { get; set; }
}
