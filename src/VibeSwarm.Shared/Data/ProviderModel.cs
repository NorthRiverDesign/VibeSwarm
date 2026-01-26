using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Represents an AI model available through a provider.
/// Models are discovered and stored when refreshing provider capabilities.
/// </summary>
public class ProviderModel
{
	public Guid Id { get; set; } = Guid.NewGuid();

	/// <summary>
	/// The provider this model belongs to
	/// </summary>
	public Guid ProviderId { get; set; }

	/// <summary>
	/// Navigation property to the parent provider
	/// </summary>
	public Providers.Provider? Provider { get; set; }

	/// <summary>
	/// Model identifier (e.g., "claude-sonnet-4-20250514", "gpt-4o")
	/// </summary>
	[Required]
	[StringLength(200)]
	public string ModelId { get; set; } = string.Empty;

	/// <summary>
	/// Display name for the model (e.g., "Claude Sonnet 4")
	/// </summary>
	[StringLength(200)]
	public string? DisplayName { get; set; }

	/// <summary>
	/// Optional description of the model's capabilities
	/// </summary>
	[StringLength(500)]
	public string? Description { get; set; }

	/// <summary>
	/// Whether this is the default model for the provider
	/// </summary>
	public bool IsDefault { get; set; }

	/// <summary>
	/// Whether the model is currently available/enabled
	/// </summary>
	public bool IsAvailable { get; set; } = true;

	/// <summary>
	/// Price multiplier relative to base pricing (1.0 = base, 5.0 = 5x more expensive)
	/// </summary>
	public decimal? PriceMultiplier { get; set; }

	/// <summary>
	/// Maximum context window size in tokens (if known)
	/// </summary>
	public int? MaxContextTokens { get; set; }

	/// <summary>
	/// When this model record was last updated
	/// </summary>
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
