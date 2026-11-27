using VibeSwarm.Shared.Data;

namespace VibeSwarm.Client.Models;

/// <summary>
/// The project roles an inference provider fills alone: suggesting ideas and writing commit
/// messages. Each keeps its profile's provider and model beside the profile's id.
/// </summary>
public static class ProjectInferenceRoles
{
	/// <summary>An inference profile whose provider is turned on.</summary>
	public static bool CanAnswerAlone(Profile profile)
		=> profile.InferenceProviderId.HasValue && profile.InferenceProvider is { IsEnabled: true };

	/// <summary>Hands a picked profile's id, provider and model to the setting, or clears all three.</summary>
	public static void Apply(Profile? profile, Action<Guid?, Guid?, string?> set)
		=> set(profile?.Id, profile?.InferenceProviderId, profile?.ModelId);
}
