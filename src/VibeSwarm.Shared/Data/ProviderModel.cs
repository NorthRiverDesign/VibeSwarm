using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Represents an AI model available through a provider.
/// Models are discovered and stored when refreshing provider capabilities.
/// </summary>
public class ProviderModel
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid ProviderId { get; set; }
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

	[StringLength(500)]
	public string? Description { get; set; }
	public bool IsDefault { get; set; }
	public bool IsAvailable { get; set; } = true;

	/// <summary>
	/// Price multiplier relative to base pricing (1.0 = base, 5.0 = 5x more expensive)
	/// </summary>
	public decimal? PriceMultiplier { get; set; }

	public int? MaxContextTokens { get; set; }

	/// <summary>
	/// Maximum output tokens per response (if known, e.g. 128k for Opus 4.6 / Sonnet 4.6)
	/// </summary>
	public int? MaxOutputTokens { get; set; }

	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

	/// <summary>
	/// Upstream retirement date announced by the provider (null when evergreen).
	/// When set, the model picker displays a "Retires YYYY-MM-DD" badge.
	/// </summary>
	public DateTime? RetiresOn { get; set; }
}
