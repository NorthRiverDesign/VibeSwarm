using VibeSwarm.Shared.LocalInference;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Runtime inference operations — health checks, model discovery, and completion generation.
/// </summary>
public interface IInferenceService
{
	/// <summary>
	/// Tests connectivity to an inference provider endpoint.
	/// </summary>
	Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, CancellationToken ct = default);

	/// <summary>
	/// Discovers available models from an inference provider.
	/// </summary>
	Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, CancellationToken ct = default);

	/// <summary>
	/// Generates a completion using the specified request parameters.
	/// </summary>
	Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default);

	/// <summary>
	/// Generates a completion using the model assigned to the given task type.
	/// Resolves provider config and model assignment automatically.
	/// </summary>
	Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default);
}
