using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Summary of job statistics for a project
/// </summary>
public class ProjectJobStats
{
    public Guid ProjectId { get; set; }
    public int TotalJobs { get; set; }
    public int CompletedJobs { get; set; }
    public int FailedJobs { get; set; }
    public int ActiveJobs { get; set; }
    public int TotalIdeas { get; set; }
    public int UnprocessedIdeas { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
}

/// <summary>
/// Project with its aggregated job statistics
/// </summary>
public class ProjectWithStats
{
    public required Project Project { get; set; }
    public ProjectJobStats Stats { get; set; } = new();
    public string? CurrentBranch { get; set; }
    public JobSummary? LatestJob { get; set; }
}

/// <summary>
/// Dashboard project information with latest job details
/// </summary>
public class DashboardProjectInfo
{
    public required Project Project { get; set; }

    /// <summary>
    /// Current Git branch (if working path is a git repository)
    /// </summary>
    public string? CurrentBranch { get; set; }

    /// <summary>
    /// Latest job for the project (if any)
    /// </summary>
    public JobSummary? LatestJob { get; set; }
}

public class DashboardRunningJobInfo
{
	public required Project Project { get; set; }
	public required JobSummary Job { get; set; }
}

/// <summary>
/// Aggregated dashboard metrics for completed jobs within a selected time window.
/// </summary>
public class DashboardJobMetrics
{
    public int RangeDays { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int TotalCompletedJobs { get; set; }
    public double? AverageDurationSeconds { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public List<DashboardJobMetricsBucket> Buckets { get; set; } = [];
}

/// <summary>
/// One chart bucket for dashboard job metrics.
/// </summary>
public class DashboardJobMetricsBucket
{
    public DateTime BucketStartUtc { get; set; }
    public int CompletedJobs { get; set; }
    public double? AverageDurationSeconds { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
}

public interface IProjectService
{
    Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default);
    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default);
	Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default);
	Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default);
	Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
	Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult(new GitHubRepositoryBrowserResult
		{
			ErrorMessage = "GitHub repository browsing is not available."
		});

    /// <summary>
    /// Get all projects with their aggregated job statistics
    /// </summary>
    Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get active dashboard projects with their latest job, ordered for dashboard display.
    /// </summary>
    Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get dashboard-ready aggregates for completed jobs and duration trends.
    /// </summary>
    Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default);

	/// <summary>
	/// Get one currently running job per active project for the dashboard.
	/// </summary>
	Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken cancellationToken = default);
}
