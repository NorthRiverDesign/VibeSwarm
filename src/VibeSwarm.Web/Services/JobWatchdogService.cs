using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Background service that monitors jobs for stalls, orphaned processes, and performs recovery.
/// Uses the database as the source of truth to detect jobs that have stopped responding.
/// </summary>
public class JobWatchdogService : BackgroundService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<JobWatchdogService> _logger;
	private readonly IJobUpdateService? _jobUpdateService;
	private readonly string _workerInstanceId;

	/// <summary>
	/// How long a job can go without a heartbeat before it's considered stalled
	/// </summary>
	private readonly TimeSpan _stallThreshold = TimeSpan.FromMinutes(2);

	/// <summary>
	/// How long a job can be in cancellation requested state before force cancellation
	/// </summary>
	private readonly TimeSpan _forceCancelThreshold = TimeSpan.FromSeconds(30);

	/// <summary>
	/// How often to check for stalled/orphaned jobs
	/// </summary>
	private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(10);

	public JobWatchdogService(
		IServiceScopeFactory scopeFactory,
		ILogger<JobWatchdogService> logger,
		IJobUpdateService? jobUpdateService = null)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
		_jobUpdateService = jobUpdateService;
		_workerInstanceId = JobProcessingService.GetWorkerInstanceId();
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("Job Watchdog Service started (Worker: {WorkerId})", _workerInstanceId);

		// Wait a bit before first check to let jobs start up
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
		}
		catch (OperationCanceledException)
		{
			_logger.LogInformation("Job Watchdog Service cancelled during startup");
			return;
		}

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await CheckForStalledJobsAsync(stoppingToken);
				await CheckForForceCancellationAsync(stoppingToken);
				await CheckForOrphanedJobsAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in job watchdog check");
			}

			try
			{
				await Task.Delay(_checkInterval, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		_logger.LogInformation("Job Watchdog Service stopped");
	}

	/// <summary>
	/// Check for jobs that have stopped sending heartbeats but are still marked as running
	/// </summary>
	private async Task CheckForStalledJobsAsync(CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var jobService = scope.ServiceProvider.GetRequiredService<IJobService>();

		var cutoffTime = DateTime.UtcNow - _stallThreshold;

		// Find jobs that are running but haven't had activity in a while
		// Only consider jobs owned by THIS worker instance
		var stalledJobs = await dbContext.Jobs
			.Where(j => (j.Status == JobStatus.Started || j.Status == JobStatus.Processing))
			.Where(j => j.WorkerInstanceId == _workerInstanceId)
			.Where(j => j.LastHeartbeatAt.HasValue && j.LastHeartbeatAt.Value < cutoffTime)
			.ToListAsync(cancellationToken);

		foreach (var job in stalledJobs)
		{
			_logger.LogWarning(
				"Job {JobId} appears stalled (no heartbeat since {LastHeartbeat}). Retry count: {RetryCount}/{MaxRetries}. ProcessId: {ProcessId}",
				job.Id, job.LastHeartbeatAt, job.RetryCount, job.MaxRetries, job.ProcessId);

			// Try to kill the process if we have a PID
			if (job.ProcessId.HasValue)
			{
				await TryKillProcessAsync(job.ProcessId.Value);
			}
			else
			{
				_logger.LogWarning("Job {JobId} stalled but no process ID was captured. The CLI process may have failed to start.", job.Id);
			}

			// Check if we should retry or fail
			if (job.MaxRetries == 0 || job.RetryCount < job.MaxRetries)
			{
				// Reset job for retry
				job.Status = JobStatus.New;
				job.RetryCount++;
				job.StartedAt = null;
				job.WorkerInstanceId = null;
				job.LastHeartbeatAt = null;
				job.ProcessId = null;
				job.CurrentActivity = null;

				// Provide detailed error message based on whether process started
				var errorDetails = job.ProcessId.HasValue
					? "CLI process started but became unresponsive"
					: "CLI process failed to start - check that the executable is accessible";
				job.ErrorMessage = $"Job stalled after {_stallThreshold.TotalMinutes:F0} minutes without activity. {errorDetails}. Retry {job.RetryCount}/{job.MaxRetries}";

				_logger.LogInformation("Job {JobId} reset for retry (attempt {RetryCount})", job.Id, job.RetryCount);
			}
			else
			{
				// Exceeded retry limit, mark as failed
				job.Status = JobStatus.Failed;
				job.CompletedAt = DateTime.UtcNow;
				job.WorkerInstanceId = null;
				job.ProcessId = null;
				job.CurrentActivity = null;
				job.ErrorMessage = $"Job failed after {job.RetryCount} retry attempts. Last failure: stalled without activity.";

				_logger.LogError("Job {JobId} permanently failed after {RetryCount} retries", job.Id, job.RetryCount);
			}

			await dbContext.SaveChangesAsync(cancellationToken);
			await NotifyJobStatusChangedAsync(job.Id, job.Status);
		}
	}

	/// <summary>
	/// Check for jobs that have been waiting for cancellation too long and need force termination
	/// </summary>
	private async Task CheckForForceCancellationAsync(CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var cutoffTime = DateTime.UtcNow - _forceCancelThreshold;

		// Find jobs that have been in cancellation requested state too long
		var hangingCancelJobs = await dbContext.Jobs
			.Where(j => (j.Status == JobStatus.Started || j.Status == JobStatus.Processing))
			.Where(j => j.CancellationRequested)
			.Where(j => j.WorkerInstanceId == _workerInstanceId)
			.Where(j => j.LastActivityAt.HasValue && j.LastActivityAt.Value < cutoffTime)
			.ToListAsync(cancellationToken);

		foreach (var job in hangingCancelJobs)
		{
			_logger.LogWarning("Force cancelling job {JobId} that didn't respond to cancellation request", job.Id);

			// Force kill the process
			if (job.ProcessId.HasValue)
			{
				await TryKillProcessAsync(job.ProcessId.Value);
			}

			job.Status = JobStatus.Cancelled;
			job.CompletedAt = DateTime.UtcNow;
			job.WorkerInstanceId = null;
			job.ProcessId = null;
			job.CurrentActivity = null;
			job.ErrorMessage = "Job was force-cancelled after not responding to cancellation request.";

			await dbContext.SaveChangesAsync(cancellationToken);
			await NotifyJobCompletedAsync(job.Id, false, job.ErrorMessage);
		}
	}

	/// <summary>
	/// Check for jobs that were assigned to a worker that is no longer running.
	/// This handles crash recovery scenarios.
	/// </summary>
	private async Task CheckForOrphanedJobsAsync(CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		// Get all active workers by checking recent heartbeats from jobs
		var activeWorkerCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);

		// Find jobs that are running but assigned to workers that haven't sent heartbeats recently
		// AND are not our worker (we handle our own jobs in CheckForStalledJobsAsync)
		var orphanedJobs = await dbContext.Jobs
			.Where(j => (j.Status == JobStatus.Started || j.Status == JobStatus.Processing))
			.Where(j => !string.IsNullOrEmpty(j.WorkerInstanceId))
			.Where(j => j.WorkerInstanceId != _workerInstanceId)
			.Where(j => !j.LastHeartbeatAt.HasValue || j.LastHeartbeatAt.Value < activeWorkerCutoff)
			.ToListAsync(cancellationToken);

		foreach (var job in orphanedJobs)
		{
			_logger.LogWarning(
				"Job {JobId} appears orphaned (worker {WorkerId} not responding). Recovering...",
				job.Id, job.WorkerInstanceId);

			// Check if we should retry or fail
			if (job.MaxRetries == 0 || job.RetryCount < job.MaxRetries)
			{
				// Reset job for retry
				job.Status = JobStatus.New;
				job.RetryCount++;
				job.StartedAt = null;
				job.WorkerInstanceId = null;
				job.LastHeartbeatAt = null;
				job.ProcessId = null;
				job.CurrentActivity = null;
				job.ErrorMessage = $"Worker crashed or became unresponsive. Retry {job.RetryCount}/{job.MaxRetries}";

				_logger.LogInformation("Orphaned job {JobId} recovered and queued for retry", job.Id);
			}
			else
			{
				// Exceeded retry limit
				job.Status = JobStatus.Failed;
				job.CompletedAt = DateTime.UtcNow;
				job.WorkerInstanceId = null;
				job.ProcessId = null;
				job.CurrentActivity = null;
				job.ErrorMessage = $"Job failed after {job.RetryCount} retry attempts. Last failure: worker became unresponsive.";

				_logger.LogError("Orphaned job {JobId} permanently failed after {RetryCount} retries", job.Id, job.RetryCount);
			}

			await dbContext.SaveChangesAsync(cancellationToken);
			await NotifyJobStatusChangedAsync(job.Id, job.Status);
		}
	}

	private async Task TryKillProcessAsync(int processId)
	{
		_logger.LogInformation("Attempting to kill process {ProcessId} (Platform: {Platform})",
			processId, PlatformHelper.OsDescription);

		var success = PlatformHelper.TryKillProcessTree(processId, msg => _logger.LogDebug("{Message}", msg));

		if (success)
		{
			_logger.LogInformation("Successfully terminated process {ProcessId}", processId);
		}
		else
		{
			_logger.LogWarning("Failed to terminate process {ProcessId}", processId);
		}

		// Small delay to ensure process cleanup
		await Task.Delay(100);
	}

	private async Task NotifyJobStatusChangedAsync(Guid jobId, JobStatus status)
	{
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyJobStatusChanged(jobId, status.ToString());
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to notify job status change for {JobId}", jobId);
			}
		}
	}

	private async Task NotifyJobCompletedAsync(Guid jobId, bool success, string? errorMessage = null)
	{
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyJobCompleted(jobId, success, errorMessage);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to notify job completion for {JobId}", jobId);
			}
		}
	}
}
