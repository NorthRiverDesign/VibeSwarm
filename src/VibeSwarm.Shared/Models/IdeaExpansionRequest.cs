namespace VibeSwarm.Shared.Models;

/// <summary>
/// Request model for expanding an idea with AI.
/// Allows specifying whether to use inference and which model/provider to use.
/// </summary>
public class IdeaExpansionRequest
{
	/// <summary>
	/// When true, uses a inference provider (e.g., Ollama) instead of a CLI coding provider.
	/// </summary>
	public bool UseInference { get; set; }

	/// <summary>
	/// The model name to use for expansion. If null, the default model is used.
	/// For inference, this is the Ollama model name (e.g., "llama3.2").
	/// For CLI providers, this may be used when the provider supports model selection.
	/// </summary>
	public string? ModelName { get; set; }

	/// <summary>
	/// The CLI provider ID to use for expansion. If null, the default provider is used.
	/// Only used when UseInference is false.
	/// </summary>
	public Guid? ProviderId { get; set; }
}
