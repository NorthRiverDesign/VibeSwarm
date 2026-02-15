using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Represents an AI model available from a local inference provider.
/// Models can be assigned to specific task types (e.g., commit messages, summarization).
/// </summary>
public class InferenceModel
{
	public Guid Id { get; set; } = Guid.NewGuid();

	/// <summary>
	/// The inference provider this model belongs to
	/// </summary>
	public Guid InferenceProviderId { get; set; }

	/// <summary>
	/// Navigation property to the parent inference provider
	/// </summary>
	public InferenceProvider? InferenceProvider { get; set; }

	/// <summary>
	/// Model identifier as reported by the provider, e.g. "llama3.2", "qwen2.5-coder:7b"
	/// </summary>
	[Required]
	[StringLength(200)]
	public string ModelId { get; set; } = string.Empty;

	/// <summary>
	/// Optional display name for the model
	/// </summary>
	[StringLength(200)]
	public string? DisplayName { get; set; }

	/// <summary>
	/// Model parameter size, e.g. "7B", "13B"
	/// </summary>
	[StringLength(50)]
	public string? ParameterSize { get; set; }

	/// <summary>
	/// Model family, e.g. "llama", "qwen"
	/// </summary>
	[StringLength(100)]
	public string? Family { get; set; }

	/// <summary>
	/// Quantization level, e.g. "Q4_0", "Q5_K_M"
	/// </summary>
	[StringLength(50)]
	public string? QuantizationLevel { get; set; }

	/// <summary>
	/// Size of the model in bytes
	/// </summary>
	public long? SizeBytes { get; set; }

	/// <summary>
	/// The task type this model is assigned to.
	/// "default" serves as fallback when no specific task model is assigned.
	/// </summary>
	[Required]
	[StringLength(100)]
	public string TaskType { get; set; } = "default";

	/// <summary>
	/// Whether this is the default model for its assigned task type
	/// </summary>
	public bool IsDefault { get; set; }

	/// <summary>
	/// Whether the model is currently available on the provider
	/// </summary>
	public bool IsAvailable { get; set; } = true;

	/// <summary>
	/// When this model record was last updated
	/// </summary>
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
