using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Manages job queue with priority scheduling and fair distribution across projects.
/// Provides thread-safe access to pending jobs with support for dependencies.
/// </summary>
public class JobQueueManager
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<JobQueueManager>? _logger;
	private readonly SemaphoreSlim _queueLock = new(1, 1);
	private readonly ConcurrentDictionary<Guid, DateTime> _recentlyDequeued = new();

	/// <summary>
	/// Time to wait before allowing a job to be dequeued again if it wasn't claimed
	/// </summary>
	public TimeSpan RequeueDelay { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Maximum number of jobs from the same project to return in a single batch.
	/// Set to 1 to ensure only one job runs per project at a time.
	/// </summary>
	public int MaxJobsPerProject { get; set; } = 1;

	public JobQueueManager(IServiceScopeFactory scopeFactory, ILogger<JobQueueManager>? logger = null)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	/// <summary>
	/// Gets pending jobs ordered by priority and creation time, with fair distribution.
	/// Only returns jobs for projects that don't already have a running job.
	/// </summary>
	public async Task<IReadOnlyList<Job>> GetPendingJobsAsync(int maxJobs, CancellationToken cancellationToken = default)
	{
		await _queueLock.WaitAsync(cancellationToken);
		try
		{
			CleanupRecentlyDequeued();

			using var scope = _scopeFactory.CreateScope();
			var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

			// Get projects that already have a running job (Started, Processing, or Paused)
			var projectsWithRunningJobs = await dbContext.Jobs
				.Where(j => j.Status == JobStatus.Started || j.Status == JobStatus.Processing || j.Status == JobStatus.Paused)
				.Select(j => j.ProjectId)
				.Distinct()
				.ToListAsync(cancellationToken);

			// Get all pending jobs that aren't blocked and whose project doesn't have a running job
			var pendingJobs = await dbContext.Jobs
				.Include(j => j.Project)
				.Include(j => j.Provider)
				.Where(j => j.Status == JobStatus.New && !j.CancellationRequested)
				.Where(j => !projectsWithRunningJobs.Contains(j.ProjectId))
				.OrderByDescending(j => j.Priority)
				.ThenBy(j => j.CreatedAt)
				.ToListAsync(cancellationToken);

			// Filter out recently dequeued jobs
			var eligibleJobs = pendingJobs
				.Where(j => !_recentlyDequeued.ContainsKey(j.Id))
				.ToList();

			// Filter out jobs with unsatisfied dependencies
			eligibleJobs = FilterByDependencies(eligibleJobs, dbContext);

			// Apply fair distribution across projects (one job per project)
			var result = ApplyFairDistribution(eligibleJobs, maxJobs);

			// Track dequeued jobs
			foreach (var job in result)
			{
				_recentlyDequeued[job.Id] = DateTime.UtcNow;
			}

			_logger?.LogDebug("Returning {Count} pending jobs from queue (total pending: {Total}, projects with running jobs: {RunningProjects})",
				result.Count, pendingJobs.Count, projectsWithRunningJobs.Count);

			return result;
		}
		finally
		{
			_queueLock.Release();
		}
	}

	/// <summary>
	/// Marks a job as successfully claimed (removes from recently dequeued tracking)
	/// </summary>
	public void MarkJobClaimed(Guid jobId)
	{
		_recentlyDequeued.TryRemove(jobId, out _);
	}

	/// <summary>
	/// Marks a job as failed to claim (allows it to be dequeued again sooner)
	/// </summary>
	public void MarkJobNotClaimed(Guid jobId)
	{
		// Update timestamp to allow sooner re-dequeue
		_recentlyDequeued.TryRemove(jobId, out _);
	}

	/// <summary>
	/// Gets the count of pending jobs
	/// </summary>
	public async Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		return await dbContext.Jobs
			.Where(j => j.Status == JobStatus.New && !j.CancellationRequested)
			.CountAsync(cancellationToken);
	}

	/// <summary>
	/// Gets queue statistics
	/// </summary>
	public async Task<QueueStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var jobs = await dbContext.Jobs
			.AsNoTracking()
			.ToListAsync(cancellationToken);

		var stats = new QueueStatistics
		{
			TotalJobs = jobs.Count,
			PendingJobs = jobs.Count(j => j.Status == JobStatus.New),
			RunningJobs = jobs.Count(j => j.Status == JobStatus.Started || j.Status == JobStatus.Processing),
			CompletedJobs = jobs.Count(j => j.Status == JobStatus.Completed),
			FailedJobs = jobs.Count(j => j.Status == JobStatus.Failed),
			CancelledJobs = jobs.Count(j => j.Status == JobStatus.Cancelled),
			StalledJobs = jobs.Count(j => j.Status == JobStatus.Stalled)
		};

		// Calculate jobs by priority
		stats.JobsByPriority = jobs
			.Where(j => j.Status == JobStatus.New)
			.GroupBy(j => j.Priority)
			.ToDictionary(g => g.Key, g => g.Count());

		// Calculate jobs by project
		stats.JobsByProject = jobs
			.Where(j => j.Status == JobStatus.New)
			.GroupBy(j => j.ProjectId)
			.ToDictionary(g => g.Key, g => g.Count());

		// Calculate average wait time for recently completed jobs
		var recentCompleted = jobs
			.Where(j => j.Status == JobStatus.Completed && j.CompletedAt.HasValue && j.StartedAt.HasValue)
			.OrderByDescending(j => j.CompletedAt)
			.Take(100)
			.ToList();

		if (recentCompleted.Any())
		{
			stats.AverageWaitTime = TimeSpan.FromSeconds(
				recentCompleted.Average(j => (j.StartedAt!.Value - j.CreatedAt).TotalSeconds));
			stats.AverageExecutionTime = TimeSpan.FromSeconds(
				recentCompleted.Average(j => (j.CompletedAt!.Value - j.StartedAt!.Value).TotalSeconds));
		}

		return stats;
	}

	/// <summary>
	/// Filters jobs based on their dependencies
	/// </summary>
	private List<Job> FilterByDependencies(List<Job> jobs, VibeSwarmDbContext dbContext)
	{
		var result = new List<Job>();

		foreach (var job in jobs)
		{
			if (!job.DependsOnJobId.HasValue)
			{
				// No dependency, can run
				result.Add(job);
				continue;
			}

			// Check if dependent job is completed
			var dependsOn = dbContext.Jobs.Find(job.DependsOnJobId.Value);
			if (dependsOn != null && dependsOn.Status == JobStatus.Completed)
			{
				result.Add(job);
			}
			else
			{
				_logger?.LogDebug("Job {JobId} skipped - waiting for dependency {DependencyId}",
					job.Id, job.DependsOnJobId.Value);
			}
		}

		return result;
	}

	/// <summary>
	/// Applies fair distribution to prevent a single project from hogging resources
	/// </summary>
	private List<Job> ApplyFairDistribution(List<Job> jobs, int maxJobs)
	{
		var result = new List<Job>();
		var jobCountByProject = new Dictionary<Guid, int>();

		foreach (var job in jobs)
		{
			if (result.Count >= maxJobs)
				break;

			var projectId = job.ProjectId;
			if (!jobCountByProject.TryGetValue(projectId, out var count))
			{
				count = 0;
			}

			if (count < MaxJobsPerProject)
			{
				result.Add(job);
				jobCountByProject[projectId] = count + 1;
			}
		}

		// If we still have room and skipped some jobs, add them
		if (result.Count < maxJobs)
		{
			foreach (var job in jobs)
			{
				if (result.Count >= maxJobs)
					break;

				if (!result.Contains(job))
				{
					result.Add(job);
				}
			}
		}

		return result;
	}

	private void CleanupRecentlyDequeued()
	{
		var cutoff = DateTime.UtcNow - RequeueDelay;
		var toRemove = _recentlyDequeued
			.Where(kvp => kvp.Value < cutoff)
			.Select(kvp => kvp.Key)
			.ToList();

		foreach (var key in toRemove)
		{
			_recentlyDequeued.TryRemove(key, out _);
		}
	}
}

/// <summary>
/// Statistics about the job queue
/// </summary>
public class QueueStatistics
{
	public int TotalJobs { get; set; }
	public int PendingJobs { get; set; }
	public int RunningJobs { get; set; }
	public int CompletedJobs { get; set; }
	public int FailedJobs { get; set; }
	public int CancelledJobs { get; set; }
	public int StalledJobs { get; set; }
	public TimeSpan AverageWaitTime { get; set; }
	public TimeSpan AverageExecutionTime { get; set; }
	public Dictionary<int, int> JobsByPriority { get; set; } = new();
	public Dictionary<Guid, int> JobsByProject { get; set; } = new();
}
