using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Represents a skill that can be exposed to AI agents via MCP (Model Context Protocol).
/// Skills define capabilities and knowledge that providers can call upon during job execution.
/// </summary>
public class Skill
{
	public Guid Id { get; set; }

	/// <summary>
	/// The name of the skill, used as the MCP tool identifier.
	/// </summary>
	[Required]
	[StringLength(100, MinimumLength = 1)]
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// A brief description of what the skill does, shown to AI agents.
	/// </summary>
	[StringLength(500)]
	public string? Description { get; set; }

	/// <summary>
	/// The markdown content defining the skill's instructions and capabilities.
	/// This is provided to AI agents when they invoke the skill.
	/// </summary>
	[Required]
	public string Content { get; set; } = string.Empty;

	/// <summary>
	/// Whether this skill is enabled and should be exposed to providers.
	/// </summary>
	public bool IsEnabled { get; set; } = true;

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }
}
