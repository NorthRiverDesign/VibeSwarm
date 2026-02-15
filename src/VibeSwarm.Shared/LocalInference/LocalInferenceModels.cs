namespace VibeSwarm.Shared.LocalInference;

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

/// <summary>
/// Result of a health/connectivity check against an inference provider.
/// </summary>
public class InferenceHealthResult
{
	public bool IsAvailable { get; set; }
	public string? Version { get; set; }
	public string? Error { get; set; }
	public List<DiscoveredModel> DiscoveredModels { get; set; } = [];
}

/// <summary>
/// A request to generate a completion from an inference provider.
/// </summary>
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
	/// Optional: explicitly specify the endpoint. If null, the service resolves from provider config.
	/// </summary>
	public string? Endpoint { get; set; }
}

/// <summary>
/// The result of a completion generation from an inference provider.
/// </summary>
public class InferenceResponse
{
	public bool Success { get; set; }
	public string? Response { get; set; }
	public string? Error { get; set; }
	public string? ModelUsed { get; set; }

	/// <summary>
	/// Total generation time in milliseconds
	/// </summary>
	public long? DurationMs { get; set; }

	/// <summary>
	/// Number of tokens in the prompt
	/// </summary>
	public int? PromptTokens { get; set; }

	/// <summary>
	/// Number of tokens generated in the response
	/// </summary>
	public int? CompletionTokens { get; set; }
}
