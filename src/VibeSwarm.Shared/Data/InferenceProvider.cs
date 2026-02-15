using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.LocalInference;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Represents a local inference provider (e.g., Ollama) used for lightweight AI tasks
/// such as generating commit messages, summarization, etc.
/// Separate from the main coding Provider system.
/// </summary>
public class InferenceProvider
{
	public Guid Id { get; set; } = Guid.NewGuid();

	/// <summary>
	/// Friendly name for this provider instance, e.g. "My Local Ollama"
	/// </summary>
	[Required]
	[StringLength(100, MinimumLength = 1)]
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// The type of inference provider (Ollama, LMStudio, etc.)
	/// </summary>
	[Required]
	public InferenceProviderType ProviderType { get; set; } = InferenceProviderType.Ollama;

	/// <summary>
	/// Base endpoint URL, e.g. "http://localhost:11434"
	/// </summary>
	[Required]
	[StringLength(500)]
	[Url]
	public string Endpoint { get; set; } = string.Empty;

	/// <summary>
	/// Optional API key for providers that require authentication
	/// </summary>
	[StringLength(200)]
	public string? ApiKey { get; set; }

	/// <summary>
	/// Whether this provider is enabled for use
	/// </summary>
	public bool IsEnabled { get; set; } = true;

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }

	/// <summary>
	/// Available models discovered from this provider
	/// </summary>
	public ICollection<InferenceModel> Models { get; set; } = new List<InferenceModel>();
}
