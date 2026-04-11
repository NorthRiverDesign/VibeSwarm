using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

public class ProjectProvider
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public Guid ProjectId { get; set; }

	[JsonIgnore]
	public Project? Project { get; set; }

	public Guid ProviderId { get; set; }

	public Provider? Provider { get; set; }

	[Range(0, 1000)]
	public int Priority { get; set; }

	public bool IsEnabled { get; set; } = true;

	[StringLength(200)]
	public string? PreferredModelId { get; set; }

	[StringLength(VibeSwarm.Shared.Validation.ValidationLimits.ReasoningEffortMaxLength)]
	public string? PreferredReasoningEffort { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }
}
