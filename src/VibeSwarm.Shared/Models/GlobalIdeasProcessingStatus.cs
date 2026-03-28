namespace VibeSwarm.Shared.Models;

public class GlobalIdeasProcessingStatus
{
	public int TotalProjectsWithIdeas { get; set; }
	public int TotalUnprocessedIdeas { get; set; }
	public int ProjectsCurrentlyProcessing { get; set; }
	public List<ProjectIdeasSummary> Projects { get; set; } = [];
}

public class ProjectIdeasSummary
{
	public Guid ProjectId { get; set; }
	public string ProjectName { get; set; } = string.Empty;
	public int UnprocessedIdeas { get; set; }
	public bool IsProcessing { get; set; }
}
