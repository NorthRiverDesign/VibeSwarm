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

	/// <summary>The picked provider: <c>p:&lt;id&gt;</c> for a coding provider, <c>i:&lt;id&gt;</c> for an inference one.</summary>
	[Required(ErrorMessage = "Choose a provider.")]
	public string? ProviderKey { get; set; }

	public bool IsProviderDefault { get; set; }

	public Guid? ProviderId => ParseKey("p:");

	public Guid? InferenceProviderId => ParseKey("i:");

	public static string CodingKey(Guid id) => $"p:{id}";

	public static string InferenceKey(Guid id) => $"i:{id}";

	private Guid? ParseKey(string prefix)
		=> ProviderKey != null && ProviderKey.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(ProviderKey[prefix.Length..], out var id)
			? id
			: null;

	public string? ModelId { get; set; }

	public string? ReasoningEffort { get; set; }

	public static ProfileFormModel From(Profile? profile) => profile == null
		? new ProfileFormModel()
		: new ProfileFormModel
		{
			Id = profile.Id,
			Name = profile.Name,
			Description = profile.Description,
			ProviderKey = profile.ProviderId is Guid providerId
				? CodingKey(providerId)
				: profile.InferenceProviderId is Guid inferenceId ? InferenceKey(inferenceId) : null,
			IsProviderDefault = profile.IsProviderDefault,
			ModelId = profile.ModelId,
			ReasoningEffort = profile.ReasoningEffort,
		};

	public Profile ToProfile() => new()
	{
		Id = Id,
		Name = Name.Trim(),
		Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
		ProviderId = ProviderId,
		InferenceProviderId = InferenceProviderId,
		ModelId = string.IsNullOrWhiteSpace(ModelId) ? null : ModelId.Trim(),
		ReasoningEffort = InferenceProviderId.HasValue ? null : ProviderCapabilities.NormalizeReasoningEffort(ReasoningEffort),
	};
}
