using System.Net.Http.Json;
using VibeSwarm.Shared.Inference;
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

		return await _http.GetJsonAsync(url, new InferenceHealthResult(), ct);
	}

	public async Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, CancellationToken ct = default)
	{
		var health = await CheckHealthAsync(endpoint, ct);
		return health.DiscoveredModels;
	}

	public async Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
	{
		try
		{
			var response = await _http.PostAsJsonAsync("/api/inference/generate", request, ct);

			if (!response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
				return new InferenceResponse
				{
					Success = false,
					Error = $"Server returned {(int)response.StatusCode}: {(string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body)}"
				};
			}

			return await response.ReadJsonAsync(new InferenceResponse { Success = false, Error = "Empty response from server" }, ct);
		}
		catch (OperationCanceledException)
		{
			return new InferenceResponse { Success = false, Error = "Request was cancelled or timed out." };
		}
		catch (Exception ex)
		{
			return new InferenceResponse { Success = false, Error = ex.Message };
		}
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
