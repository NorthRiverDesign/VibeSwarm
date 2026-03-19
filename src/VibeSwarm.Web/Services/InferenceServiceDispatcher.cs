using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Routes inference requests to the correct backend (Ollama, Grok, etc.)
/// based on the provider type specified in the request or resolved from the database.
/// </summary>
public class InferenceServiceDispatcher : IInferenceService
{
	private readonly OllamaInferenceService _ollama;
	private readonly GrokInferenceService _grok;
	private readonly IInferenceProviderService _providerService;

	public InferenceServiceDispatcher(
		OllamaInferenceService ollama,
		GrokInferenceService grok,
		IInferenceProviderService providerService)
	{
		_ollama = ollama;
		_grok = grok;
		_providerService = providerService;
	}

	public async Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
	{
		var resolved = providerType ?? await ResolveProviderTypeByEndpointAsync(endpoint, ct);
		return await GetService(resolved).CheckHealthAsync(endpoint, resolved, ct);
	}

	public async Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
	{
		var resolved = providerType ?? await ResolveProviderTypeByEndpointAsync(endpoint, ct);
		return await GetService(resolved).GetAvailableModelsAsync(endpoint, resolved, ct);
	}

	public async Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
	{
		var providerType = request.ProviderType
			?? await ResolveProviderTypeByEndpointAsync(request.Endpoint, ct);
		return await GetService(providerType).GenerateAsync(request, ct);
	}

	public async Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default)
	{
		// For task-based generation, resolve the provider from the task's model assignment
		var model = await _providerService.GetModelForTaskAsync(taskType, ct);
		if (model != null)
		{
			var provider = await ResolveProviderByModelAsync(model, ct);
			if (provider != null)
			{
				return await GetService(provider.Value).GenerateForTaskAsync(taskType, prompt, systemPrompt, ct);
			}
		}

		// Default to Ollama for backwards compatibility
		return await _ollama.GenerateForTaskAsync(taskType, prompt, systemPrompt, ct);
	}

	private IInferenceService GetService(InferenceProviderType providerType)
	{
		return providerType switch
		{
			InferenceProviderType.Grok => _grok,
			_ => _ollama
		};
	}

	private async Task<InferenceProviderType> ResolveProviderTypeByEndpointAsync(string? endpoint, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(endpoint))
		{
			return InferenceProviderType.Ollama;
		}

		var providers = await _providerService.GetEnabledAsync(ct);
		var match = providers.FirstOrDefault(p =>
			string.Equals(p.Endpoint?.TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

		return match?.ProviderType ?? InferenceProviderType.Ollama;
	}

	private async Task<InferenceProviderType?> ResolveProviderByModelAsync(InferenceModel model, CancellationToken ct)
	{
		var provider = await _providerService.GetByIdAsync(model.InferenceProviderId, ct);
		return provider?.ProviderType;
	}
}
