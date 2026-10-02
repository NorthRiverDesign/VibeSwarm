using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

public enum IdeaExpansionStatus
{
	NotExpanded,

	Expanding,

	PendingReview,

	Approved,

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

	[Required]
	[StringLength(ValidationLimits.IdeaDescriptionMaxLength, MinimumLength = 1)]
	public string Description { get; set; } = string.Empty;

	/// <summary>
	/// AI-expanded detailed specification of the idea.
	/// Generated when user requests expansion before converting to a job.
	/// </summary>
	[StringLength(ValidationLimits.IdeaExpandedDescriptionMaxLength)]
	public string? ExpandedDescription { get; set; }

	public IdeaExpansionStatus ExpansionStatus { get; set; } = IdeaExpansionStatus.NotExpanded;

	[StringLength(ValidationLimits.IdeaExpansionErrorMaxLength)]
	public string? ExpansionError { get; set; }
	public DateTime? ExpandedAt { get; set; }

	/// <summary>
	/// Order for processing ideas (lower numbers processed first)
	/// </summary>
	public int SortOrder { get; set; }

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

	public bool HasExpandedDescription => !string.IsNullOrWhiteSpace(ExpandedDescription) &&
		ExpansionStatus == IdeaExpansionStatus.Approved;

	public ICollection<IdeaAttachment> Attachments { get; set; } = new List<IdeaAttachment>();
}

public class IdeaAttachment
{
	public Guid Id { get; set; }
	public Guid IdeaId { get; set; }
	public Idea? Idea { get; set; }

	[Required]
	[StringLength(ValidationLimits.IdeaAttachmentFileNameMaxLength, MinimumLength = 1)]
	public string FileName { get; set; } = string.Empty;

	[StringLength(ValidationLimits.IdeaAttachmentContentTypeMaxLength)]
	public string? ContentType { get; set; }

	[Required]
	[StringLength(ValidationLimits.IdeaAttachmentRelativePathMaxLength, MinimumLength = 1)]
	public string RelativePath { get; set; } = string.Empty;
	public long SizeBytes { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
