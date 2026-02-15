using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.LocalInference;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Ollama-specific implementation of inference operations.
/// Communicates with the Ollama HTTP API for model discovery and text generation.
/// </summary>
public class OllamaInferenceService : IInferenceService
{
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IInferenceProviderService _providerService;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public OllamaInferenceService(IHttpClientFactory httpClientFactory, IInferenceProviderService providerService)
	{
		_httpClientFactory = httpClientFactory;
		_providerService = providerService;
	}

	public async Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, CancellationToken ct = default)
	{
		var result = new InferenceHealthResult();
		endpoint = NormalizeEndpoint(endpoint ?? await ResolveEndpointAsync(ct));

		try
		{
			using var client = CreateClient(TimeSpan.FromSeconds(5));

			// Check if Ollama is responding
			var response = await client.GetAsync(endpoint, ct);
			if (response.IsSuccessStatusCode)
			{
				var body = await response.Content.ReadAsStringAsync(ct);
				result.IsAvailable = true;
				result.Version = body.Contains("Ollama") ? body.Trim() : "Ollama";
			}

			// Discover models
			if (result.IsAvailable)
			{
				result.DiscoveredModels = await GetAvailableModelsAsync(endpoint, ct);
			}
		}
		catch (Exception ex)
		{
			result.IsAvailable = false;
			result.Error = ex.Message;
		}

		return result;
	}

	public async Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, CancellationToken ct = default)
	{
		endpoint = NormalizeEndpoint(endpoint ?? await ResolveEndpointAsync(ct));
		var models = new List<DiscoveredModel>();

		try
		{
			using var client = CreateClient(TimeSpan.FromSeconds(10));
			var response = await client.GetAsync($"{endpoint}/api/tags", ct);
			response.EnsureSuccessStatusCode();

			var tagsResponse = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(JsonOptions, ct);
			if (tagsResponse?.Models != null)
			{
				foreach (var m in tagsResponse.Models)
				{
					models.Add(new DiscoveredModel
					{
						Name = m.Name ?? string.Empty,
						DisplayName = m.Name,
						SizeBytes = m.Size,
						ParameterSize = m.Details?.ParameterSize,
						Family = m.Details?.Family,
						QuantizationLevel = m.Details?.QuantizationLevel,
						ModifiedAt = m.ModifiedAt
					});
				}
			}
		}
		catch
		{
			// Return empty list on failure
		}

		return models;
	}

	public async Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
	{
		var endpoint = NormalizeEndpoint(request.Endpoint ?? await ResolveEndpointAsync(ct));
		var model = request.Model;

		if (string.IsNullOrEmpty(model))
		{
			var resolved = await _providerService.GetModelForTaskAsync(request.TaskType, ct);
			model = resolved?.ModelId;
		}

		if (string.IsNullOrEmpty(model))
		{
			return new InferenceResponse
			{
				Success = false,
				Error = "No model configured for this task type. Please configure a default model in Settings."
			};
		}

		try
		{
			using var client = CreateClient(TimeSpan.FromSeconds(120));

			var ollamaRequest = new OllamaGenerateRequest
			{
				Model = model,
				Prompt = request.Prompt,
				System = request.SystemPrompt,
				Stream = false,
				Options = (request.Temperature.HasValue || request.MaxTokens.HasValue)
					? new OllamaOptions
					{
						Temperature = request.Temperature,
						NumPredict = request.MaxTokens
					}
					: null
			};

			var response = await client.PostAsJsonAsync($"{endpoint}/api/generate", ollamaRequest, JsonOptions, ct);
			response.EnsureSuccessStatusCode();

			var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonOptions, ct);

			return new InferenceResponse
			{
				Success = true,
				Response = ollamaResponse?.Response,
				ModelUsed = model,
				DurationMs = ollamaResponse?.TotalDuration.HasValue == true
					? ollamaResponse.TotalDuration.Value / 1_000_000 // nanoseconds to ms
					: null,
				PromptTokens = ollamaResponse?.PromptEvalCount,
				CompletionTokens = ollamaResponse?.EvalCount
			};
		}
		catch (Exception ex)
		{
			return new InferenceResponse
			{
				Success = false,
				Error = ex.Message,
				ModelUsed = model
			};
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

	private HttpClient CreateClient(TimeSpan timeout)
	{
		var client = _httpClientFactory.CreateClient("LocalInference");
		client.Timeout = timeout;
		return client;
	}

	private async Task<string> ResolveEndpointAsync(CancellationToken ct)
	{
		var providers = await _providerService.GetEnabledAsync(ct);
		var provider = providers.FirstOrDefault();
		return provider?.Endpoint ?? "http://localhost:11434";
	}

	private static string NormalizeEndpoint(string endpoint)
		=> endpoint.TrimEnd('/');

	// ---- Ollama API JSON types (private) ----

	private class OllamaTagsResponse
	{
		public List<OllamaModel>? Models { get; set; }
	}

	private class OllamaModel
	{
		public string? Name { get; set; }
		public long? Size { get; set; }
		public DateTime? ModifiedAt { get; set; }
		public OllamaModelDetails? Details { get; set; }
	}

	private class OllamaModelDetails
	{
		public string? ParameterSize { get; set; }
		public string? Family { get; set; }
		public string? QuantizationLevel { get; set; }
	}

	private class OllamaGenerateRequest
	{
		public string Model { get; set; } = string.Empty;
		public string Prompt { get; set; } = string.Empty;
		public string? System { get; set; }
		public bool Stream { get; set; }
		public OllamaOptions? Options { get; set; }
	}

	private class OllamaOptions
	{
		public double? Temperature { get; set; }
		public int? NumPredict { get; set; }
	}

	private class OllamaGenerateResponse
	{
		public string? Response { get; set; }
		public long? TotalDuration { get; set; }
		public int? PromptEvalCount { get; set; }
		public int? EvalCount { get; set; }
	}
}
