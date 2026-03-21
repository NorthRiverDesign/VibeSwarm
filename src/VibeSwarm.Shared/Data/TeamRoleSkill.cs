using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

public class TeamRoleSkill
{
	public Guid TeamRoleId { get; set; }

	[JsonIgnore]
	public TeamRole? TeamRole { get; set; }

	public Guid SkillId { get; set; }

	public Skill? Skill { get; set; }
}
