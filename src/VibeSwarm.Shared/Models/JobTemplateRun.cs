using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

/// <summary>Runs a template on each of the listed projects.</summary>
public sealed class JobTemplateRunRequest
{
	public List<Guid> ProjectIds { get; set; } = [];
}

/// <summary>What a run did on one project: the job it queued, or why it skipped the project.</summary>
public sealed class JobTemplateRunOutcome
{
	public Guid ProjectId { get; set; }
	public string ProjectName { get; set; } = string.Empty;
	public Guid? JobId { get; set; }
	public string? SkipReason { get; set; }
}

public sealed class JobTemplateRunResult
{
	public List<JobTemplateRunOutcome> Projects { get; set; } = [];
	public int QueuedCount => Projects.Count(project => project.JobId.HasValue);
	public int SkippedCount => Projects.Count(project => !project.JobId.HasValue);
}

/// <summary>The latest job a template created on one project.</summary>
public sealed class JobTemplateProjectRun
{
	public Guid ProjectId { get; set; }
	public string ProjectName { get; set; } = string.Empty;
	public Guid JobId { get; set; }
	public JobStatus Status { get; set; }
	public DateTime CreatedAt { get; set; }
	public DateTime? CompletedAt { get; set; }
	public bool IsUnfinished => JobTemplateRunRules.IsUnfinished(Status);
}

public static class JobTemplateRunRules
{
	/// <summary>A job that is queued, running or waiting, so a second run on its project would duplicate it.</summary>
	public static bool IsUnfinished(JobStatus status)
		=> status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled);
}
