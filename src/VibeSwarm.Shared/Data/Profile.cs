using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// A named combination of a coding provider, a model and a reasoning level, picked wherever a job,
/// a planner, a reviewer or the Lead needs one. Project settings that link a profile copy its
/// values, and saving the profile writes the new values through to every link.
/// </summary>
public class Profile
{
	public Guid Id { get; set; }

	[Required]
	[StringLength(ValidationLimits.ProfileNameMaxLength, MinimumLength = 1)]
	public string Name { get; set; } = string.Empty;

	[StringLength(ValidationLimits.ProfileDescriptionMaxLength)]
	public string? Description { get; set; }

	public Guid ProviderId { get; set; }

	public Provider? Provider { get; set; }

	/// <summary>The model to run. Null uses the provider's default.</summary>
	[StringLength(ValidationLimits.ProjectPlanningModelIdMaxLength)]
	public string? ModelId { get; set; }

	/// <summary>The reasoning level to run at. Null uses the provider's default.</summary>
	[StringLength(ValidationLimits.ReasoningEffortMaxLength)]
	public string? ReasoningEffort { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }
}
