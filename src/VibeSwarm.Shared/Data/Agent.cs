using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

public class Agent
{
	public Guid Id { get; set; }

	[Required]
	[StringLength(100, MinimumLength = 1)]
	public string Name { get; set; } = string.Empty;

	[StringLength(Validation.ValidationLimits.AgentDescriptionMaxLength)]
	public string? Description { get; set; }

	[StringLength(Validation.ValidationLimits.AgentResponsibilitiesMaxLength)]
	public string? Responsibilities { get; set; }

	public Guid? DefaultProviderId { get; set; }

	public Provider? DefaultProvider { get; set; }

	[StringLength(200)]
	public string? DefaultModelId { get; set; }

	[StringLength(Validation.ValidationLimits.ReasoningEffortMaxLength)]
	public string? DefaultReasoningEffort { get; set; }

	public CycleMode DefaultCycleMode { get; set; } = CycleMode.SingleCycle;

	public CycleSessionMode DefaultCycleSessionMode { get; set; } = CycleSessionMode.ContinueSession;

	[Range(1, 100)]
	public int DefaultMaxCycles { get; set; } = 1;

	[StringLength(Validation.ValidationLimits.JobTemplatePromptMaxLength)]
	public string? DefaultCycleReviewPrompt { get; set; }

	public bool IsEnabled { get; set; } = true;

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }

	public ICollection<AgentSkill> SkillLinks { get; set; } = new List<AgentSkill>();

	public ICollection<ProjectAgent> ProjectAssignments { get; set; } = new List<ProjectAgent>();
}
