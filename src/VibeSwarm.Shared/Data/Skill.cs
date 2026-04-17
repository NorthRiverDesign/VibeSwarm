using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

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
	[StringLength(ValidationLimits.SkillDescriptionMaxLength)]
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

	/// <summary>
	/// Where this skill was installed from. Defaults to <see cref="SkillSourceType.Manual"/> for
	/// hand-authored skills and for legacy rows created before install metadata existed.
	/// </summary>
	public SkillSourceType SourceType { get; set; } = SkillSourceType.Manual;

	/// <summary>
	/// Human-readable identifier for the source: GitHub subpath (e.g. <c>anthropics/skills/pdf</c>),
	/// local filesystem path, or uploaded ZIP filename. Null for manually authored skills.
	/// </summary>
	[StringLength(500)]
	public string? SourceUri { get; set; }

	/// <summary>
	/// Commit SHA (marketplace) or timestamp/version identifier (other sources) captured at install time,
	/// used to detect upstream drift when re-installing.
	/// </summary>
	[StringLength(100)]
	public string? SourceRef { get; set; }

	/// <summary>
	/// Absolute path to the directory containing this skill's <c>SKILL.md</c> and any
	/// <c>references/</c> or <c>scripts/</c> subtrees. Null for legacy skills that exist only
	/// as <see cref="Content"/>; those are materialized on first access.
	/// </summary>
	[StringLength(1000)]
	public string? StoragePath { get; set; }

	/// <summary>
	/// Raw <c>allowed-tools</c> value parsed from SKILL.md frontmatter. Surfaced in the system
	/// prompt so the agent can honor the skill's declared tool restrictions.
	/// </summary>
	[StringLength(2000)]
	public string? AllowedTools { get; set; }

	/// <summary>
	/// True when the installed skill folder contains files marked executable or under a
	/// <c>scripts/</c> subtree. Drives the security-warning surface at install time.
	/// </summary>
	public bool HasScripts { get; set; }

	/// <summary>
	/// When the skill was installed from an external source. Null for manually authored skills
	/// (use <see cref="CreatedAt"/> instead).
	/// </summary>
	public DateTime? InstalledAt { get; set; }
}
