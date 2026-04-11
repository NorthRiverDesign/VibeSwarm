using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

public class TeamRole
{
	public Guid Id { get; set; }

	[Required]
	[StringLength(100, MinimumLength = 1)]
	public string Name { get; set; } = string.Empty;

	[StringLength(Validation.ValidationLimits.TeamRoleDescriptionMaxLength)]
	public string? Description { get; set; }

	[StringLength(Validation.ValidationLimits.TeamRoleResponsibilitiesMaxLength)]
	public string? Responsibilities { get; set; }

	public Guid? DefaultProviderId { get; set; }

	public Provider? DefaultProvider { get; set; }

	[StringLength(200)]
	public string? DefaultModelId { get; set; }

	[StringLength(Validation.ValidationLimits.ReasoningEffortMaxLength)]
	public string? DefaultReasoningEffort { get; set; }

	public bool IsEnabled { get; set; } = true;

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }

	public ICollection<TeamRoleSkill> SkillLinks { get; set; } = new List<TeamRoleSkill>();

	public ICollection<ProjectTeamRole> ProjectAssignments { get; set; } = new List<ProjectTeamRole>();
}
