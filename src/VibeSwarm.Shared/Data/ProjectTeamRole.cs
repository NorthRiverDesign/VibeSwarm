using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

public class ProjectTeamRole
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public Guid ProjectId { get; set; }

	[JsonIgnore]
	public Project? Project { get; set; }

	public Guid TeamRoleId { get; set; }

	public TeamRole? TeamRole { get; set; }

	public Guid ProviderId { get; set; }

	public Provider? Provider { get; set; }

	[StringLength(200)]
	public string? PreferredModelId { get; set; }

	public bool IsEnabled { get; set; } = true;

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }
}
