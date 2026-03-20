using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared;

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
	private readonly IVersionControlService _versionControlService;
	private readonly string _workerInstanceId;

	/// <summary>
	/// Default stall threshold for SDK providers with streaming (regular events expected)
	/// </summary>
	private readonly TimeSpan _sdkStallThreshold = TimeSpan.FromMinutes(3);

	/// <summary>
	/// Default stall threshold for CLI providers (model loading on low-powered hardware)
	/// </summary>
	private readonly TimeSpan _cliStallThreshold = TimeSpan.FromMinutes(10);

	/// <summary>
	/// Give long-running Claude CLI tool executions more time before considering them stalled.
	/// </summary>
	private readonly TimeSpan _claudeToolExecutionThreshold = TimeSpan.FromMinutes(30);

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
		IVersionControlService versionControlService,
		IJobUpdateService? jobUpdateService = null)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
		_versionControlService = versionControlService;
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
	/// <summary>
	/// Gets the stall threshold for a job based on its provider configuration.
	/// Priority: Provider.StallTimeoutSeconds > connection-mode default
	/// </summary>
	private TimeSpan GetStallThreshold(Job job)
	{
		// User override on the provider entity
		if (job.Provider?.StallTimeoutSeconds is > 0)
		{
			return TimeSpan.FromSeconds(job.Provider.StallTimeoutSeconds.Value);
		}

		// Provider-aware defaults
		return job.Provider?.ConnectionMode switch
		{
			ProviderConnectionMode.SDK => _sdkStallThreshold,
			_ => _cliStallThreshold
		};
	}

	private TimeSpan GetEffectiveStallThreshold(Job job)
	{
		var threshold = GetStallThreshold(job);
		if (job.Provider?.Type == ProviderType.Claude
			&& !string.IsNullOrWhiteSpace(job.CurrentActivity)
			&& job.CurrentActivity.StartsWith("Running tool:", StringComparison.OrdinalIgnoreCase))
		{
			return threshold < _claudeToolExecutionThreshold
				? _claudeToolExecutionThreshold
				: threshold;
		}

		return threshold;
	}

	private async Task CheckForStalledJobsAsync(CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		// Use the most conservative (longest) threshold to fetch candidates,
		// then apply per-provider thresholds in memory
		var maxCutoffTime = DateTime.UtcNow - _cliStallThreshold;

		// Find jobs that are running but haven't had activity in a while
		// Only consider jobs owned by THIS worker instance
		var candidateJobs = await dbContext.Jobs
			.Include(j => j.Provider)
			.Include(j => j.Project)
			.Where(j => (j.Status == JobStatus.Started || j.Status == JobStatus.Processing))
			.Where(j => j.WorkerInstanceId == _workerInstanceId)
			.Where(j => j.LastHeartbeatAt.HasValue && j.LastHeartbeatAt.Value < maxCutoffTime)
			.ToListAsync(cancellationToken);

		// Apply per-provider stall thresholds
		var stalledJobs = candidateJobs
			.Where(j => j.LastHeartbeatAt!.Value < DateTime.UtcNow - GetEffectiveStallThreshold(j))
			.ToList();

		foreach (var job in stalledJobs)
		{
			var threshold = GetEffectiveStallThreshold(job);
			var providerInfo = job.Provider != null ? $"{job.Provider.Name} ({job.Provider.ConnectionMode})" : "unknown";

			_logger.LogWarning(
				"Job {JobId} appears stalled (no heartbeat since {LastHeartbeat}, threshold: {Threshold}s, provider: {Provider}). Retry count: {RetryCount}/{MaxRetries}. ProcessId: {ProcessId}",
				job.Id, job.LastHeartbeatAt, (int)threshold.TotalSeconds, providerInfo, job.RetryCount, job.MaxRetries, job.ProcessId);

			// Graduated response: first detection = warn, 2x threshold = take action
			var timeSinceHeartbeat = DateTime.UtcNow - job.LastHeartbeatAt!.Value;
			if (timeSinceHeartbeat < threshold * 2 && !job.ErrorMessage?.Contains("Stall detected") == true)
			{
				// First detection: warn but don't kill yet
				_logger.LogWarning("Stall detected for job {JobId}, monitoring... (will act at {ActionTime})",
					job.Id, job.LastHeartbeatAt.Value + threshold * 2);

				job.CurrentActivity = "Stall detected, monitoring...";
				job.ErrorMessage = "Stall detected, monitoring...";
				await dbContext.SaveChangesAsync(cancellationToken);

				if (_jobUpdateService != null)
				{
					await _jobUpdateService.NotifyJobActivity(job.Id, "Stall detected, monitoring...", DateTime.UtcNow);
				}
				continue;
			}

			// Try to kill the process if we have a PID
			if (job.ProcessId.HasValue)
			{
				await TryKillProcessAsync(job.ProcessId.Value);
			}
			else
			{
				_logger.LogWarning("Job {JobId} stalled but no process ID was captured. The CLI process may have failed to start.", job.Id);
			}

			if (await TryPreserveChangesForRecoveryAsync(job,
				$"Job stalled after {threshold.TotalMinutes:F0} minutes without activity.",
				cancellationToken))
			{
				await dbContext.SaveChangesAsync(cancellationToken);
				await NotifyJobStatusChangedAsync(job.Id, job.Status);
				continue;
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
				job.ErrorMessage = $"Job stalled after {threshold.TotalMinutes:F0} minutes without activity. {errorDetails}. Retry {job.RetryCount}/{job.MaxRetries}";

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
		var activeWorkerCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);

		// Find jobs that are running but assigned to workers that haven't sent heartbeats recently
		// AND are not our worker (we handle our own jobs in CheckForStalledJobsAsync)
		var orphanedJobs = await dbContext.Jobs
			.Include(j => j.Project)
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

			if (await TryPreserveChangesForRecoveryAsync(job,
				"Worker crashed or became unresponsive before job changes were finalized.",
				cancellationToken))
			{
				await dbContext.SaveChangesAsync(cancellationToken);
				await NotifyJobStatusChangedAsync(job.Id, job.Status);
				continue;
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

	private async Task<bool> TryPreserveChangesForRecoveryAsync(Job job, string reason, CancellationToken cancellationToken)
	{
		var workingDirectory = job.Project?.WorkingPath;
		if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
		{
			return false;
		}

		if (!await _versionControlService.IsGitRepositoryAsync(workingDirectory, cancellationToken))
		{
			return false;
		}

		var workingTreeStatus = await _versionControlService.GetWorkingTreeStatusAsync(workingDirectory, cancellationToken);
		if (!workingTreeStatus.HasUncommittedChanges)
		{
			return false;
		}

		var diff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, job.GitCommitBefore, cancellationToken)
			?? await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, cancellationToken: cancellationToken);

		var preserveResult = await _versionControlService.PreserveChangesAsync(
			workingDirectory,
			$"{AppConstants.AppName} job {job.Id}: {reason}",
			cancellationToken);

		if (!preserveResult.Success)
		{
			_logger.LogWarning("Failed to preserve workspace changes for job {JobId}: {Error}", job.Id, preserveResult.Error);
			return false;
		}

		var transition = JobStateMachine.TryTransition(job, JobStatus.Stalled, reason);
		if (!transition.Success)
		{
			_logger.LogWarning("Failed to transition job {JobId} to stalled recovery state: {Error}", job.Id, transition.ErrorMessage);
			return false;
		}

		job.GitDiff = !string.IsNullOrWhiteSpace(diff) ? diff : job.GitDiff;
		job.ChangedFilesCount = workingTreeStatus.ChangedFilesCount;
		job.WorkerInstanceId = null;
		job.LastHeartbeatAt = null;
		job.ProcessId = null;
		job.CurrentActivity = null;
		job.CancellationRequested = false;
		job.ErrorMessage = $"{reason} Preserved {workingTreeStatus.ChangedFilesCount} changed file(s) in {preserveResult.SavedReference ?? "stash@{0}"} for recovery.";

		_logger.LogWarning("Preserved {Count} changed file(s) for job {JobId} in {Reference}",
			workingTreeStatus.ChangedFilesCount, job.Id, preserveResult.SavedReference ?? "stash@{0}");

		return true;
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

		// Handle idea state when job completes (reset IsProcessing for failed jobs, remove for successful ones)
		try
		{
			using var scope = _scopeFactory.CreateScope();
			var ideaService = scope.ServiceProvider.GetRequiredService<IIdeaService>();
			await ideaService.HandleJobCompletionAsync(jobId, success);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to handle idea completion for job {JobId}", jobId);
		}
	}
}
