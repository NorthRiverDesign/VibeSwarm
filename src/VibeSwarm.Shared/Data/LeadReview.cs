using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

public enum LeadReviewStatus
{
	Running = 0,
	Completed = 1,
	Failed = 2,
	Cancelled = 3
}

/// <summary>
/// One pass of a project's Lead: a read-only look over the repository and its recent history that
/// ends in a short report and ranked ideas, which wait in the Ideas list for the owner's approval.
/// </summary>
public class LeadReview
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public Guid ProjectId { get; set; }
	public Project? Project { get; set; }

	public LeadReviewStatus Status { get; set; } = LeadReviewStatus.Running;

	/// <summary>What started it: the owner, or auto-pilot running out of approved ideas.</summary>
	[StringLength(50)]
	public string? Trigger { get; set; }

	[StringLength(200)]
	public string? ProviderName { get; set; }

	[StringLength(ValidationLimits.ProjectPlanningModelIdMaxLength)]
	public string? ModelId { get; set; }

	/// <summary>The report written for the owner, without the ideas.</summary>
	[StringLength(LeadReviewLimits.ContentMaxLength)]
	public string? Content { get; set; }

	public int IdeasCreated { get; set; }

	[StringLength(2000)]
	public string? Error { get; set; }

	public int? InputTokens { get; set; }
	public int? OutputTokens { get; set; }
	public decimal? CostUsd { get; set; }
	public long? DurationMs { get; set; }

	public Guid? PreviousReviewId { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime? CompletedAt { get; set; }
}

public static class LeadReviewLimits
{
	public const int ContentMaxLength = 20_000;
	public const int DirectionMaxLength = 4_000;
	public const int MaxIdeasPerReview = 8;
}
