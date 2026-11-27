using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Models;

public static class ProjectExecutionDefaults
{
	/// <summary>
	/// The profiles a job may run on, in the order to offer them: the project's Build order
	/// first, then each provider's default profile, then the rest by name.
	/// </summary>
	public static List<Profile> OrderJobProfiles(Project? project, IEnumerable<Profile> profiles)
	{
		var buildOrder = (project?.ProviderSelections ?? [])
			.Where(selection => selection.IsEnabled && selection.ProfileId.HasValue)
			.OrderBy(selection => selection.Priority)
			.Select(selection => selection.ProfileId!.Value)
			.ToList();
		return profiles
			.Where(ProfileRoles.CanBuild)
			.OrderBy(profile => buildOrder.IndexOf(profile.Id) is var index and >= 0 ? index : int.MaxValue)
			.ThenByDescending(profile => profile.IsProviderDefault)
			.ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	/// <summary>
	/// The profile a new job on the project starts on: its first Build entry's, as the server
	/// picks for a job that names none. A project with no Build order starts on the default
	/// provider's own profile.
	/// </summary>
	public static Guid? GetBuildProfileId(Project? project, IEnumerable<Profile> profiles)
	{
		var ordered = OrderJobProfiles(project, profiles);
		var listed = (project?.ProviderSelections ?? [])
			.Where(selection => selection.IsEnabled)
			.OrderBy(selection => selection.Priority)
			.Select(selection => ordered.FirstOrDefault(profile => profile.Id == selection.ProfileId))
			.FirstOrDefault(profile => profile != null);
		return (listed
			?? ordered.Where(profile => profile.IsProviderDefault).OrderByDescending(profile => profile.Provider?.IsDefault == true).FirstOrDefault()
			?? ordered.FirstOrDefault())?.Id;
	}

public static List<Provider> GetAllowedProviders(Project? project, IEnumerable<Provider> providers)
{
var enabledProviders = providers
.Where(provider => provider.IsEnabled)
.OrderByDescending(provider => provider.IsDefault)
.ThenBy(provider => provider.Name)
.ToList();

var selections = project?.ProviderSelections?
.Where(selection => selection.IsEnabled)
.OrderBy(selection => selection.Priority)
.ToList();
if (selections == null || selections.Count == 0)
{
return enabledProviders;
}

var providerById = enabledProviders.ToDictionary(provider => provider.Id);
return selections
.Where(selection => providerById.ContainsKey(selection.ProviderId))
.Select(selection => providerById[selection.ProviderId])
.ToList();
}

public static Provider? GetPreferredProvider(Project? project, IEnumerable<Provider> providers)
{
return GetAllowedProviders(project, providers).FirstOrDefault();
}

public static string ResolveModelId(Project? project, Guid providerId, IReadOnlyList<ProviderModel> models)
{
var availableModels = models
.Where(model => model.IsAvailable)
.ToList();
var preferredModelId = project?.ProviderSelections?
.Where(selection => selection.IsEnabled && selection.ProviderId == providerId)
.OrderBy(selection => selection.Priority)
.Select(selection => selection.PreferredModelId)
.FirstOrDefault(modelId => !string.IsNullOrWhiteSpace(modelId));

if (!string.IsNullOrWhiteSpace(preferredModelId) &&
availableModels.Any(model => string.Equals(model.ModelId, preferredModelId, StringComparison.Ordinal)))
{
return preferredModelId;
}

	return availableModels.FirstOrDefault(model => model.IsDefault)?.ModelId ?? string.Empty;
}

public static string ResolveReasoningEffort(Project? project, Provider provider)
{
	var preferredReasoning = project?.ProviderSelections?
		.Where(selection => selection.IsEnabled && selection.ProviderId == provider.Id)
		.OrderBy(selection => selection.Priority)
		.Select(selection => selection.PreferredReasoningEffort)
		.FirstOrDefault(reasoning => !string.IsNullOrWhiteSpace(reasoning));

	return ProviderCapabilities.NormalizeReasoningEffort(preferredReasoning ?? provider.DefaultReasoningEffort) ?? string.Empty;
}
}
