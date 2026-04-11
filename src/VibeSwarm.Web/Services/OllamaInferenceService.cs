using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
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
	private readonly RuntimeOptions _runtimeOptions;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public OllamaInferenceService(
		IHttpClientFactory httpClientFactory,
		IInferenceProviderService providerService,
		RuntimeOptions? runtimeOptions = null)
	{
		_httpClientFactory = httpClientFactory;
		_providerService = providerService;
		_runtimeOptions = runtimeOptions ?? new RuntimeOptions();
	}

	public async Task<InferenceHealthResult> CheckHealthAsync(string? endpoint = null, InferenceProviderType? providerType = null, CancellationToken ct = default)
	{
		var result = new InferenceHealthResult();
		endpoint = NormalizeEndpoint(endpoint ?? await ResolveEndpointAsync(ct));

		try
		{
			using var client = CreateClient(_runtimeOptions.HealthCheckTimeout);

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
				result.DiscoveredModels = await GetAvailableModelsAsync(endpoint, ct: ct);
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
		var models = new List<DiscoveredModel>();

		try
		{
			using var client = CreateClient(_runtimeOptions.ModelDiscoveryTimeout);
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
		var provider = await ResolveRequestedProviderAsync(request, ct);
		var endpoint = NormalizeEndpoint(request.Endpoint ?? provider?.Endpoint ?? await ResolveEndpointAsync(ct));
		var model = await ResolveModelAsync(request, provider, ct);

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
			var ollamaRequest = new OllamaGenerateRequest
			{
				Model = model,
				Prompt = request.Prompt,
				System = request.SystemPrompt,
				Stream = true,
				Options = (request.Temperature.HasValue || request.MaxTokens.HasValue)
					? new OllamaOptions
					{
						Temperature = request.Temperature,
						NumPredict = request.MaxTokens
					}
					: null
			};

			return await GenerateStreamingAsync(endpoint, model, ollamaRequest, ct);
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

	private HttpClient CreateClient(TimeSpan timeout)
	{
		var client = _httpClientFactory.CreateClient("Inference");
		client.Timeout = timeout;
		return client;
	}

	private async Task<InferenceResponse> GenerateStreamingAsync(
		string endpoint,
		string model,
		OllamaGenerateRequest ollamaRequest,
		CancellationToken ct)
	{
		using var client = CreateClient(Timeout.InfiniteTimeSpan);
		using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		generationCts.CancelAfter(_runtimeOptions.GenerationTimeout);

		using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/api/generate")
		{
			Content = new StringContent(
				JsonSerializer.Serialize(ollamaRequest, JsonOptions),
				Encoding.UTF8,
				"application/json")
		};

		try
		{
			using var response = await client.SendAsync(
				request,
				HttpCompletionOption.ResponseHeadersRead,
				generationCts.Token);

			if (!response.IsSuccessStatusCode)
			{
				return new InferenceResponse
				{
					Success = false,
					Error = await BuildHttpFailureMessageAsync(response, generationCts.Token),
					ModelUsed = model
				};
			}

			await using var responseStream = await response.Content.ReadAsStreamAsync(generationCts.Token);
			using var reader = new StreamReader(responseStream);
			var combinedResponse = new StringBuilder();
			var receivedAnyChunk = false;
			OllamaGenerateResponse? finalChunk = null;

			while (true)
			{
				string? line;
				try
				{
					var waitTimeout = receivedAnyChunk
						? _runtimeOptions.StreamInactivityTimeout
						: _runtimeOptions.InitialResponseTimeout;
					line = await ReadLineWithTimeoutAsync(reader, waitTimeout, generationCts.Token);
				}
				catch (TimeoutException ex)
				{
					return new InferenceResponse
					{
						Success = false,
						Error = ex.Message,
						ModelUsed = model
					};
				}

				if (line == null)
				{
					break;
				}

				if (string.IsNullOrWhiteSpace(line))
				{
					continue;
				}

				OllamaGenerateResponse? chunk;
				try
				{
					chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line, JsonOptions);
				}
				catch (JsonException ex)
				{
					return new InferenceResponse
					{
						Success = false,
						Error = $"Received an invalid streaming response from Ollama: {ex.Message}",
						ModelUsed = model
					};
				}

				if (chunk == null)
				{
					continue;
				}

				receivedAnyChunk = true;
				finalChunk = chunk;

				if (!string.IsNullOrEmpty(chunk.Error))
				{
					return new InferenceResponse
					{
						Success = false,
						Error = chunk.Error,
						ModelUsed = chunk.Model ?? model
					};
				}

				if (!string.IsNullOrEmpty(chunk.Response))
				{
					combinedResponse.Append(chunk.Response);
				}

				if (chunk.Done)
				{
					break;
				}
			}

			if (!receivedAnyChunk)
			{
				return new InferenceResponse
				{
					Success = false,
					Error = BuildNoResponseMessage(model),
					ModelUsed = model
				};
			}

			if (finalChunk?.Done != true)
			{
				return new InferenceResponse
				{
					Success = false,
					Error = BuildIncompleteStreamMessage(model),
					ModelUsed = finalChunk?.Model ?? model
				};
			}

			var responseText = combinedResponse.ToString();
			if (string.IsNullOrWhiteSpace(responseText))
			{
				return new InferenceResponse
				{
					Success = false,
					Error = $"Model '{finalChunk.Model ?? model}' finished without returning any text.",
					ModelUsed = finalChunk.Model ?? model
				};
			}

			return new InferenceResponse
			{
				Success = true,
				Response = responseText,
				ModelUsed = finalChunk.Model ?? model,
				DurationMs = finalChunk.TotalDuration.HasValue
					? finalChunk.TotalDuration.Value / 1_000_000
					: null,
				PromptTokens = finalChunk.PromptEvalCount,
				CompletionTokens = finalChunk.EvalCount
			};
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException) when (generationCts.IsCancellationRequested)
		{
			return new InferenceResponse
			{
				Success = false,
				Error = $"Timed out waiting for inference after {_runtimeOptions.GenerationTimeout.TotalMinutes:F0} minutes. Slow devices may need a smaller model or more available memory.",
				ModelUsed = model
			};
		}
	}

	private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout, CancellationToken ct)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeoutCts.CancelAfter(timeout);

		try
		{
			return await reader.ReadLineAsync().WaitAsync(timeoutCts.Token);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
		{
			throw new TimeoutException($"Timed out waiting {timeout.TotalMinutes:F0} minute(s) for inference provider to respond.");
		}
	}

	private static async Task<string> BuildHttpFailureMessageAsync(HttpResponseMessage response, CancellationToken ct)
	{
		try
		{
			var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
			if (!string.IsNullOrEmpty(body))
			{
				try
				{
					var errorResponse = JsonSerializer.Deserialize<OllamaGenerateResponse>(body, JsonOptions);
					if (!string.IsNullOrWhiteSpace(errorResponse?.Error))
					{
						return errorResponse.Error;
					}
				}
				catch (JsonException)
				{
					// Fall back to the raw response body below.
				}

				return $"Ollama returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body}";
			}
		}
		catch
		{
			// Fall back to status-only error.
		}

		return $"Ollama returned {(int)response.StatusCode} ({response.ReasonPhrase}).";
	}

	private string BuildNoResponseMessage(string model)
		=> $"Model '{model}' did not return any response data before the connection closed. The inference server may have crashed or restarted while loading the model.";

	private string BuildIncompleteStreamMessage(string model)
		=> $"Model '{model}' stopped before finishing its response. This often means inference server crashed, the device ran out of memory, or the Ollama process was restarted.";

	private async Task<string> ResolveEndpointAsync(CancellationToken ct)
	{
		var providers = await _providerService.GetEnabledAsync(ct);
		var provider = providers.FirstOrDefault(p => p.ProviderType == InferenceProviderType.Ollama);
		return provider?.Endpoint ?? "http://localhost:11434";
	}

	private async Task<InferenceProvider?> ResolveRequestedProviderAsync(InferenceRequest request, CancellationToken ct)
	{
		if (request.ProviderId.HasValue)
		{
			var provider = await _providerService.GetByIdAsync(request.ProviderId.Value, ct);
			if (provider?.IsEnabled == true && provider.ProviderType == InferenceProviderType.Ollama)
			{
				return provider;
			}
		}

		if (!string.IsNullOrWhiteSpace(request.Endpoint))
		{
			var normalizedEndpoint = NormalizeEndpoint(request.Endpoint);
			var providers = await _providerService.GetEnabledAsync(ct);
			return providers.FirstOrDefault(provider =>
				provider.ProviderType == InferenceProviderType.Ollama &&
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
		public string? Model { get; set; }
		public string? Response { get; set; }
		public bool Done { get; set; }
		public string? DoneReason { get; set; }
		public string? Error { get; set; }
		public long? TotalDuration { get; set; }
		public int? PromptEvalCount { get; set; }
		public int? EvalCount { get; set; }
	}

	public sealed class RuntimeOptions
	{
		public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(5);
		public TimeSpan ModelDiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(10);
		public TimeSpan InitialResponseTimeout { get; init; } = InferenceTimeouts.LocalInitialResponseTimeout;
		public TimeSpan StreamInactivityTimeout { get; init; } = InferenceTimeouts.LocalStreamInactivityTimeout;
		public TimeSpan GenerationTimeout { get; init; } = InferenceTimeouts.LocalGenerationTimeout;
	}
}
