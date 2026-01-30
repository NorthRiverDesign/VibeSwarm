using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Models;

/// <summary>
/// Combined project info with stats, branch, and latest job for display purposes
/// </summary>
public class ProjectInfoWithStats
{
	public required Project Project { get; set; }
	public ProjectJobStats Stats { get; set; } = new();
	public string? CurrentBranch { get; set; }
	public Job? LatestJob { get; set; }
}
