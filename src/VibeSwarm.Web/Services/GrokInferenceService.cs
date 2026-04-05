using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Grok (X.AI) implementation of inference operations.
/// Uses the OpenAI-compatible API at https://api.x.ai/v1.
/// </summary>
public class GrokInferenceService : IInferenceService
{
	private const string DefaultEndpoint = "https://api.x.ai/v1";

	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IInferenceProviderService _providerService;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public GrokInferenceService(
		IHttpClientFactory httpClientFactory,
		IInferenceProviderService providerService)
	{
		_httpClientFactory = httpClientFactory;
		_providerService = providerService;
	}

	public async Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
	{
		var result = new InferenceHealthResult();
		endpoint = NormalizeEndpoint(endpoint ?? await ResolveEndpointAsync(ct));
		var apiKey = await ResolveApiKeyAsync(ct);

		try
		{
			using var client = CreateClient(TimeSpan.FromSeconds(10));
			AddAuth(client, apiKey);

			var response = await client.GetAsync($"{endpoint}/models", ct);
			if (response.IsSuccessStatusCode)
			{
				result.IsAvailable = true;
				result.Version = "Grok (X.AI)";
				result.DiscoveredModels = await ParseModelsResponseAsync(response, ct);
			}
			else
			{
				result.IsAvailable = false;
				result.Error = $"X.AI returned {(int)response.StatusCode} ({response.ReasonPhrase})";
			}
		}
		catch (Exception ex)
		{
			result.IsAvailable = false;
			result.Error = ex.Message;
		}

		return result;
	}

	public async Task<List<DiscoveredModel>> GetAvailableModelsAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
	{
		endpoint = NormalizeEndpoint(endpoint ?? await ResolveEndpointAsync(ct));
		var apiKey = await ResolveApiKeyAsync(ct);

		try
		{
			using var client = CreateClient(TimeSpan.FromSeconds(10));
			AddAuth(client, apiKey);

			var response = await client.GetAsync($"{endpoint}/models", ct);
			response.EnsureSuccessStatusCode();

			return await ParseModelsResponseAsync(response, ct);
		}
		catch
		{
			return [];
		}
	}

	public async Task<InferenceResponse> GenerateAsync(InferenceRequest request, CancellationToken ct = default)
	{
		var provider = await ResolveRequestedProviderAsync(request, ct);
		var endpoint = NormalizeEndpoint(request.Endpoint ?? provider?.Endpoint ?? await ResolveEndpointAsync(ct));
		var apiKey = provider?.ApiKey ?? await ResolveApiKeyAsync(ct);
		var model = await ResolveModelAsync(request, provider, ct);

		if (string.IsNullOrEmpty(model))
		{
			return new InferenceResponse
			{
				Success = false,
				Error = "No model configured for this task type. Please configure a default model in Settings."
			};
		}

		if (string.IsNullOrEmpty(apiKey))
		{
			return new InferenceResponse
			{
				Success = false,
				Error = "No API key configured for Grok. Add your X.AI API key in the inference provider settings.",
				ModelUsed = model
			};
		}

		try
		{
			var messages = new List<ChatMessage>();

			if (!string.IsNullOrEmpty(request.SystemPrompt))
			{
				messages.Add(new ChatMessage { Role = "system", Content = request.SystemPrompt });
			}

			messages.Add(new ChatMessage { Role = "user", Content = request.Prompt });

			var chatRequest = new ChatCompletionRequest
			{
				Model = model,
				Messages = messages,
				MaxTokens = request.MaxTokens ?? 4096,
				Temperature = request.Temperature ?? 0.7
			};

			return await SendChatCompletionAsync(endpoint, apiKey, model, chatRequest, ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
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

	private async Task<InferenceResponse> SendChatCompletionAsync(
		string endpoint,
		string apiKey,
		string model,
		ChatCompletionRequest chatRequest,
		CancellationToken ct)
	{
		using var client = CreateClient(TimeSpan.FromMinutes(5));
		AddAuth(client, apiKey);

		var json = JsonSerializer.Serialize(chatRequest, JsonOptions);
		using var content = new StringContent(json, Encoding.UTF8, "application/json");

		var response = await client.PostAsync($"{endpoint}/chat/completions", content, ct);

		if (!response.IsSuccessStatusCode)
		{
			var errorBody = await response.Content.ReadAsStringAsync(ct);
			return new InferenceResponse
			{
				Success = false,
				Error = $"X.AI returned {(int)response.StatusCode}: {errorBody}",
				ModelUsed = model
			};
		}

		var chatResponse = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, ct);

		if (chatResponse == null)
		{
			return new InferenceResponse
			{
				Success = false,
				Error = "X.AI returned an empty response.",
				ModelUsed = model
			};
		}

		var responseText = chatResponse.Choices?.FirstOrDefault()?.Message?.Content;

		if (string.IsNullOrWhiteSpace(responseText))
		{
			return new InferenceResponse
			{
				Success = false,
				Error = $"Model '{chatResponse.Model ?? model}' finished without returning any text.",
				ModelUsed = chatResponse.Model ?? model
			};
		}

		return new InferenceResponse
		{
			Success = true,
			Response = responseText,
			ModelUsed = chatResponse.Model ?? model,
			PromptTokens = chatResponse.Usage?.PromptTokens,
			CompletionTokens = chatResponse.Usage?.CompletionTokens
		};
	}

	private static async Task<List<DiscoveredModel>> ParseModelsResponseAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var models = new List<DiscoveredModel>();

		try
		{
			var modelsResponse = await response.Content.ReadFromJsonAsync<ModelsListResponse>(JsonOptions, ct);
			if (modelsResponse?.Data != null)
			{
				foreach (var m in modelsResponse.Data)
				{
					models.Add(new DiscoveredModel
					{
						Name = m.Id ?? string.Empty,
						DisplayName = m.Id
					});
				}
			}
		}
		catch
		{
			// Return empty list on parse failure
		}

		return models;
	}

	private HttpClient CreateClient(TimeSpan timeout)
	{
		var client = _httpClientFactory.CreateClient("Inference");
		client.Timeout = timeout;
		return client;
	}

	private static void AddAuth(HttpClient client, string? apiKey)
	{
		if (!string.IsNullOrEmpty(apiKey))
		{
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
		}
	}

	private async Task<string> ResolveEndpointAsync(CancellationToken ct)
	{
		var providers = await _providerService.GetEnabledAsync(ct);
		var provider = providers.FirstOrDefault(p => p.ProviderType == InferenceProviderType.Grok);
		return provider?.Endpoint ?? DefaultEndpoint;
	}

	private async Task<string?> ResolveApiKeyAsync(CancellationToken ct)
	{
		var providers = await _providerService.GetEnabledAsync(ct);
		var provider = providers.FirstOrDefault(p => p.ProviderType == InferenceProviderType.Grok);
		return provider?.ApiKey;
	}

	private async Task<InferenceProvider?> ResolveRequestedProviderAsync(InferenceRequest request, CancellationToken ct)
	{
		if (request.ProviderId.HasValue)
		{
			var provider = await _providerService.GetByIdAsync(request.ProviderId.Value, ct);
			if (provider?.IsEnabled == true && provider.ProviderType == InferenceProviderType.Grok)
			{
				return provider;
			}
		}

		if (!string.IsNullOrWhiteSpace(request.Endpoint))
		{
			var normalizedEndpoint = NormalizeEndpoint(request.Endpoint);
			var providers = await _providerService.GetEnabledAsync(ct);
			return providers.FirstOrDefault(provider =>
				provider.ProviderType == InferenceProviderType.Grok &&
				string.Equals(NormalizeEndpoint(provider.Endpoint), normalizedEndpoint, StringComparison.OrdinalIgnoreCase));
		}

		return null;
	}

	private async Task<string?> ResolveModelAsync(InferenceRequest request, InferenceProvider? provider, CancellationToken ct)
	{
		if (!string.IsNullOrWhiteSpace(request.Model))
		{
			return request.Model;
		}

		var taskType = string.IsNullOrWhiteSpace(request.TaskType) ? "default" : request.TaskType;
		var providerModel = provider?.Models.FirstOrDefault(model => model.IsAvailable && model.IsDefault && model.TaskType == taskType)
			?? provider?.Models.FirstOrDefault(model => model.IsAvailable && model.IsDefault && model.TaskType == "default");
		if (!string.IsNullOrWhiteSpace(providerModel?.ModelId))
		{
			return providerModel.ModelId;
		}

		var resolved = await _providerService.GetModelForTaskAsync(taskType, ct);
		return resolved?.ModelId;
	}

	private static string NormalizeEndpoint(string endpoint)
		=> endpoint.TrimEnd('/');

	// ---- OpenAI-compatible JSON types (private) ----

	private class ChatMessage
	{
		public string Role { get; set; } = string.Empty;
		public string Content { get; set; } = string.Empty;
	}

	private class ChatCompletionRequest
	{
		public string Model { get; set; } = string.Empty;
		public List<ChatMessage> Messages { get; set; } = [];
		public int MaxTokens { get; set; }
		public double Temperature { get; set; }
	}

	private class ChatCompletionResponse
	{
		public string? Model { get; set; }
		public List<ChatChoice>? Choices { get; set; }
		public ChatUsage? Usage { get; set; }
	}

	private class ChatChoice
	{
		public ChatMessage? Message { get; set; }
	}

	private class ChatUsage
	{
		public int? PromptTokens { get; set; }
		public int? CompletionTokens { get; set; }
	}

	private class ModelsListResponse
	{
		public List<ModelEntry>? Data { get; set; }
	}

	private class ModelEntry
	{
		public string? Id { get; set; }
	}
}
