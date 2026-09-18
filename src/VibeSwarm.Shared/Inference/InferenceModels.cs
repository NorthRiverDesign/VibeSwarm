namespace VibeSwarm.Shared.Inference;

/// <summary>
/// A model discovered from an inference provider's API.
/// </summary>
public class DiscoveredModel
{
	public string Name { get; set; } = string.Empty;
	public string? DisplayName { get; set; }
	public long? SizeBytes { get; set; }
	public string? ParameterSize { get; set; }
	public string? Family { get; set; }
	public string? QuantizationLevel { get; set; }
	public DateTime? ModifiedAt { get; set; }
}

public class InferenceHealthResult
{
	public bool IsAvailable { get; set; }
	public string? Version { get; set; }
	public string? Error { get; set; }
	public List<DiscoveredModel> DiscoveredModels { get; set; } = [];
}

/// <summary>
/// A request to probe an inference provider using configuration that has not been saved yet,
/// such as the values typed into the Add Provider form.
/// </summary>
public class InferenceProbeRequest
{
	/// <summary>
	/// Endpoint to probe. When null the service falls back to the stored provider's endpoint.
	/// </summary>
	public string? Endpoint { get; set; }

	/// <summary>
	/// Provider backend to route to. When null the service infers it from the endpoint.
	/// </summary>
	public InferenceProviderType? ProviderType { get; set; }

	/// <summary>
	/// Credentials supplied by the caller. When blank the service falls back to the stored
	/// provider's key. Providers that need no key (Ollama) ignore this.
	/// </summary>
	public string? ApiKey { get; set; }
}

public class InferenceRequest
{
	public string Prompt { get; set; } = string.Empty;
	public string? SystemPrompt { get; set; }
	public string TaskType { get; set; } = "default";
	public int? MaxTokens { get; set; }
	public double? Temperature { get; set; }

	/// <summary>
	/// Optional: explicitly specify the model to use. If null, the service resolves from task type.
	/// </summary>
	public string? Model { get; set; }

	/// <summary>
	/// Optional: explicitly specify the stored provider to use.
	/// </summary>
	public Guid? ProviderId { get; set; }

	/// <summary>
	/// Optional: explicitly specify the endpoint. If null, the service resolves from provider config.
	/// </summary>
	public string? Endpoint { get; set; }

	/// <summary>
	/// Optional: explicitly specify the provider type for routing to the correct backend.
	/// </summary>
	public InferenceProviderType? ProviderType { get; set; }
}

public class InferenceResponse
{
	public bool Success { get; set; }
	public string? Response { get; set; }
	public string? Error { get; set; }
	public string? ModelUsed { get; set; }
	public long? DurationMs { get; set; }
	public int? PromptTokens { get; set; }
	public int? CompletionTokens { get; set; }
}
