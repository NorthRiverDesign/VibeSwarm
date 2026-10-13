using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// A state-of-the-projects report an inference model wrote, acting as project manager for one
/// folder. A run can build on an earlier report, so the manager keeps track of what changed.
/// </summary>
public class FolderManagerReport
{
	public Guid Id { get; set; }

	public Guid FolderId { get; set; }

	/// <summary>The report this run continued from, if any. Cleared when that report is deleted.</summary>
	public Guid? PreviousReportId { get; set; }

	/// <summary>What the user asked the manager to look at for this run.</summary>
	[StringLength(ValidationLimits.FolderManagerNotesMaxLength)]
	public string? Notes { get; set; }

	[Required]
	[StringLength(ValidationLimits.FolderManagerReportMaxLength, MinimumLength = 1)]
	public string Content { get; set; } = string.Empty;

	/// <summary>Names of the projects the report covered, comma separated.</summary>
	[StringLength(2000)]
	public string? ProjectNames { get; set; }

	[StringLength(200)]
	public string? ProviderName { get; set; }

	public InferenceProviderType? ProviderType { get; set; }

	[StringLength(200)]
	public string? ModelId { get; set; }

	public int? PromptTokens { get; set; }

	public int? CompletionTokens { get; set; }

	public long? DurationMs { get; set; }

	/// <summary>What the provider billed, when it reports a cost.</summary>
	public decimal? CostUsd { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
