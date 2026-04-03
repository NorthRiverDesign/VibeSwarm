using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

public sealed class GlobalQueueSnapshot
{
	public int RunningJobsCount { get; set; }
	public int UpcomingIdeasCount { get; set; }
	public int ProjectsCurrentlyProcessing { get; set; }
	public List<GlobalQueueJobSummary> RunningJobs { get; set; } = [];
	public List<GlobalQueueIdeaSummary> UpcomingIdeas { get; set; } = [];
}

public sealed class GlobalQueueJobSummary
{
	public Guid Id { get; set; }
	public Guid ProjectId { get; set; }
	public string ProjectName { get; set; } = string.Empty;
	public string? Title { get; set; }
	public string GoalPrompt { get; set; } = string.Empty;
	public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? GoalPrompt : Title;
	public JobStatus Status { get; set; }
	public string? ProviderName { get; set; }
	public string? CurrentActivity { get; set; }
	public DateTime CreatedAt { get; set; }
	public DateTime? StartedAt { get; set; }
}

public sealed class GlobalQueueIdeaSummary
{
	public Guid IdeaId { get; set; }
	public Guid ProjectId { get; set; }
	public string ProjectName { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public int SortOrder { get; set; }
	public DateTime CreatedAt { get; set; }
	public bool IsProjectProcessing { get; set; }
}
