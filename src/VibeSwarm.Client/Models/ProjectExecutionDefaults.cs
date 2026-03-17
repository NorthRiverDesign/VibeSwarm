using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Client.Models;

public static class ProjectExecutionDefaults
{
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
}
