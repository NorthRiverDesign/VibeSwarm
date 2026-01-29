using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

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
}
