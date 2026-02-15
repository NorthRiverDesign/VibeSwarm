using System.Net.Http.Json;
using VibeSwarm.Shared.LocalInference;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Client-side HTTP implementation for inference operations (health, discovery, generation).
/// </summary>
public class HttpInferenceService : IInferenceService
{
	private readonly HttpClient _http;

	public HttpInferenceService(HttpClient http) => _http = http;

	public async Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, CancellationToken ct = default)
	{
		var url = string.IsNullOrEmpty(endpoint)
			? "/api/inference/health"
			: $"/api/inference/health?endpoint={Uri.EscapeDataString(endpoint)}";

		return await _http.GetFromJsonAsync<InferenceHealthResult>(url, ct) ?? new InferenceHealthResult();
	}

	public async Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, CancellationToken ct = default)
	{
		var health = await CheckHealthAsync(endpoint, ct);
		return health.DiscoveredModels;
	}

	public async Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/inference/generate", request, ct);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadFromJsonAsync<InferenceResponse>(ct) ?? new InferenceResponse { Success = false, Error = "Empty response" };
	}

	public async Task<InferenceResponse> GenerateForTaskAsync(string taskType, string prompt, string? systemPrompt = null, CancellationToken ct = default)
	{
		var request = new InferenceRequest
		{
			Prompt = prompt,
			SystemPrompt = systemPrompt,
			TaskType = taskType
		};

		return await GenerateAsync(request, ct);
	}
}
