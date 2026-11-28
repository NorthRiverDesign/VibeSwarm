using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

public enum JobStepStatus
{
	Waiting = 0,
	Running = 1,
	Done = 2,
	Skipped = 3,

	/// <summary>
	/// Built by an earlier run of the job, before a follow-up was queued on it. Kept as the job's
	/// record; the follow-up is planned and built after it, and no run looks at it again.
	/// </summary>
	Earlier = 4
}

/// <summary>Who wrote a step.</summary>
public enum JobStepSource
{
	/// <summary>The Lead's plan for the job.</summary>
	Lead = 0,

	/// <summary>The Lead's check of the finished work, which found something still to do.</summary>
	Check = 1,

	/// <summary>The user, from the job's page.</summary>
	User = 2
}

/// <summary>
/// One step of a job worked by a team. The Lead plans the job into steps; the builder carries
/// each out in a session of its own, in order, and leaves a note for the next. A step is Done
/// only while its work is in the tree the next step starts from.
/// </summary>
public class JobStep
{
	public const int TitleMaxLength = 200;
	public const int TextMaxLength = 8000;
	public const int SummaryMaxLength = 4000;

	public Guid Id { get; set; } = Guid.NewGuid();

	public Guid JobId { get; set; }

	[JsonIgnore]
	public Job? Job { get; set; }

	/// <summary>Where the step comes in the job, counting from one.</summary>
	public int Order { get; set; }

	[Required]
	[StringLength(TitleMaxLength)]
	public string Title { get; set; } = string.Empty;

	/// <summary>What to build in this step.</summary>
	[StringLength(TextMaxLength)]
	public string Instructions { get; set; } = string.Empty;

	/// <summary>How to tell the step is finished; the reviewer judges the step by it.</summary>
	[StringLength(SummaryMaxLength)]
	public string? DoneWhen { get; set; }

	public JobStepStatus Status { get; set; } = JobStepStatus.Waiting;

	public JobStepSource Source { get; set; } = JobStepSource.Lead;

	/// <summary>The builder's handoff note: what the step changed and what the next one must know.</summary>
	[StringLength(SummaryMaxLength)]
	public string? Summary { get; set; }

	public JobReviewStatus ReviewStatus { get; set; } = JobReviewStatus.None;

	public int ReviewRounds { get; set; }

	[StringLength(TextMaxLength)]
	public string? ReviewNotes { get; set; }

	/// <summary>The provider session that ran the step; each step has its own.</summary>
	[StringLength(200)]
	public string? SessionId { get; set; }

	/// <summary>The commit holding the working tree as the step left it (see <see cref="Job.WorkSnapshotCommit"/>).</summary>
	[StringLength(64)]
	public string? SnapshotCommit { get; set; }

	public int? InputTokens { get; set; }

	public int? OutputTokens { get; set; }

	public decimal? CostUsd { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? StartedAt { get; set; }

	public DateTime? CompletedAt { get; set; }
}
