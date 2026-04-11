namespace VibeSwarm.Client.Models;

public sealed class ProjectProviderModelSelectionChange
{
public Guid ProviderId { get; init; }

public string? PreferredModelId { get; init; }

public string? PreferredReasoningEffort { get; init; }
}
