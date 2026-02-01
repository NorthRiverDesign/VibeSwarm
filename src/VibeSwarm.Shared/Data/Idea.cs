using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Status of the AI expansion process for an idea
/// </summary>
public enum IdeaExpansionStatus
{
	/// <summary>
	/// The idea has not been expanded by AI yet
	/// </summary>
	NotExpanded,

	/// <summary>
	/// AI expansion is currently in progress
	/// </summary>
	Expanding,

	/// <summary>
	/// AI expansion completed and is ready for user review
	/// </summary>
	PendingReview,

	/// <summary>
	/// User approved the expanded specification
	/// </summary>
	Approved,

	/// <summary>
	/// AI expansion failed
	/// </summary>
	Failed
}

/// <summary>
/// Represents a feature idea or task description for a project that can be
/// automatically expanded and turned into a Job.
/// </summary>
public class Idea
{
	public Guid Id { get; set; }

	public Guid ProjectId { get; set; }
	public Project? Project { get; set; }

	/// <summary>
	/// Short description of the feature or update idea
	/// </summary>
	[Required]
	[StringLength(2000, MinimumLength = 1)]
	public string Description { get; set; } = string.Empty;

	/// <summary>
	/// AI-expanded detailed specification of the idea.
	/// Generated when user requests expansion before converting to a job.
	/// </summary>
	[StringLength(10000)]
	public string? ExpandedDescription { get; set; }

	/// <summary>
	/// Status of the AI expansion process
	/// </summary>
	public IdeaExpansionStatus ExpansionStatus { get; set; } = IdeaExpansionStatus.NotExpanded;

	/// <summary>
	/// Error message if expansion failed
	/// </summary>
	[StringLength(1000)]
	public string? ExpansionError { get; set; }

	/// <summary>
	/// When the expansion was last updated
	/// </summary>
	public DateTime? ExpandedAt { get; set; }

	/// <summary>
	/// Order for processing ideas (lower numbers processed first)
	/// </summary>
	public int SortOrder { get; set; }

	/// <summary>
	/// When the idea was created
	/// </summary>
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	/// <summary>
	/// The Job ID that was created from this Idea (if processing has started)
	/// </summary>
	public Guid? JobId { get; set; }
	public Job? Job { get; set; }

	/// <summary>
	/// Whether this idea is currently being processed (job created and running)
	/// </summary>
	public bool IsProcessing { get; set; }

	/// <summary>
	/// Indicates whether the idea has an expanded description ready for use
	/// </summary>
	public bool HasExpandedDescription => !string.IsNullOrWhiteSpace(ExpandedDescription) &&
		ExpansionStatus == IdeaExpansionStatus.Approved;
}
