using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

public class AgentSkill
{
	public Guid AgentId { get; set; }

	[JsonIgnore]
	public Agent? Agent { get; set; }

	public Guid SkillId { get; set; }

	public Skill? Skill { get; set; }
}
