using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Client.Models;

public sealed class ProfileFormModel
{
	public Guid Id { get; set; }

	[Required(ErrorMessage = "Give the profile a name.")]
	[StringLength(ValidationLimits.ProfileNameMaxLength, ErrorMessage = "Name must be {1} characters or fewer.")]
	public string Name { get; set; } = string.Empty;

	[StringLength(ValidationLimits.ProfileDescriptionMaxLength, ErrorMessage = "Description must be {1} characters or fewer.")]
	public string? Description { get; set; }

	[Required(ErrorMessage = "Choose a provider.")]
	public Guid? ProviderId { get; set; }

	public string? ModelId { get; set; }

	public string? ReasoningEffort { get; set; }

	public static ProfileFormModel From(Profile? profile) => profile == null
		? new ProfileFormModel()
		: new ProfileFormModel
		{
			Id = profile.Id,
			Name = profile.Name,
			Description = profile.Description,
			ProviderId = profile.ProviderId == Guid.Empty ? null : profile.ProviderId,
			ModelId = profile.ModelId,
			ReasoningEffort = profile.ReasoningEffort,
		};

	public Profile ToProfile() => new()
	{
		Id = Id,
		Name = Name.Trim(),
		Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
		ProviderId = ProviderId ?? Guid.Empty,
		ModelId = string.IsNullOrWhiteSpace(ModelId) ? null : ModelId.Trim(),
		ReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(ReasoningEffort),
	};
}
