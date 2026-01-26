using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Worker;

/// <summary>
/// Monitors running jobs for completion criteria and coordinates state transitions.
/// Ensures jobs reach completion states even if providers don't report properly.
/// </summary>
public class JobCompletionMonitorService : BackgroundService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<JobCompletionMonitorService> _logger;
	private readonly IProviderHealthTracker _healthTracker;
	private readonly IJobUpdateService? _jobUpdateService;
	private readonly ProcessSupervisor _processSupervisor;
	private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(15);

	public JobCompletionMonitorService(
		IServiceScopeFactory scopeFactory,
		ILogger<JobCompletionMonitorService> logger,
		IProviderHealthTracker healthTracker,
		ProcessSupervisor processSupervisor,
		IJobUpdateService? jobUpdateService = null)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
		_healthTracker = healthTracker;
		_processSupervisor = processSupervisor;
		_jobUpdateService = jobUpdateService;

		// Subscribe to process events
		_processSupervisor.ProcessUnhealthy += OnProcessUnhealthy;
		_processSupervisor.ProcessExitedUnexpectedly += OnProcessExitedUnexpectedly;
		_processSupervisor.ProcessExited += OnProcessExited;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("Job Completion Monitor Service started");

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await CheckRunningJobsAsync(stoppingToken);
				await CheckDependentJobsAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in job completion monitor");
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

		_logger.LogInformation("Job Completion Monitor Service stopped");
	}

	/// <summary>
	/// Checks all running jobs for completion criteria
	/// </summary>
	private async Task CheckRunningJobsAsync(CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var runningJobs = await dbContext.Jobs
			.Include(j => j.Project)
			.Include(j => j.Provider)
			.Where(j => j.Status == JobStatus.Started || j.Status == JobStatus.Processing)
			.ToListAsync(cancellationToken);

		foreach (var job in runningJobs)
		{
			try
			{
				var criteria = job.GetCompletionCriteria();
				var evaluation = JobStateMachine.EvaluateCompletion(job, criteria);

				if (evaluation.IsComplete)
				{
					_logger.LogInformation("Job {JobId} met completion criteria: {Reason}",
						job.Id, evaluation.CompletionReason);

					await HandleJobCompletionAsync(job, evaluation, dbContext, cancellationToken);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error evaluating completion criteria for job {JobId}", job.Id);
			}
		}
	}

	/// <summary>
	/// Handles job completion based on evaluation
	/// </summary>
	private async Task HandleJobCompletionAsync(
		Job job,
		CompletionEvaluation evaluation,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		JobStatus newStatus;
		string? errorMessage = null;

		if (evaluation.ShouldFail)
		{
			newStatus = JobStatus.Failed;
			errorMessage = evaluation.CompletionReason;
		}
		else if (evaluation.ShouldRetry && job.RetryCount < job.MaxRetries)
		{
			newStatus = JobStatus.New;
			job.RetryCount++;
			errorMessage = $"Job will retry ({job.RetryCount}/{job.MaxRetries}): {evaluation.CompletionReason}";
			_logger.LogInformation("Job {JobId} marked for retry: {Reason}", job.Id, evaluation.CompletionReason);
		}
		else if (evaluation.ShouldRetry)
		{
			newStatus = JobStatus.Failed;
			errorMessage = $"Job exhausted retries: {evaluation.CompletionReason}";
		}
		else
		{
			newStatus = JobStatus.Completed;
		}

		// Apply state transition
		var transition = JobStateMachine.TryTransition(job, newStatus, evaluation.CompletionReason);
		if (!transition.Success)
		{
			_logger.LogWarning("Failed to transition job {JobId} to {Status}: {Error}",
				job.Id, newStatus, transition.ErrorMessage);
			return;
		}

		if (errorMessage != null)
		{
			job.ErrorMessage = errorMessage;
		}

		// If job completed successfully, try to get a session summary
		if (newStatus == JobStatus.Completed)
		{
			await TryFetchSessionSummaryAsync(job, dbContext, cancellationToken);
		}

		// Stop any supervised process
		await _processSupervisor.StopProcessAsync(job.Id, graceful: false);

		// Update provider health
		if (job.ProviderId != Guid.Empty)
		{
			if (newStatus == JobStatus.Completed)
			{
				_healthTracker.RecordSuccess(job.ProviderId,
					job.StartedAt.HasValue ? DateTime.UtcNow - job.StartedAt.Value : null);
			}
			else if (newStatus == JobStatus.Failed)
			{
				_healthTracker.RecordFailure(job.ProviderId, errorMessage);
			}

			_healthTracker.DecrementProviderLoad(job.ProviderId);
		}

		await dbContext.SaveChangesAsync(cancellationToken);

		// Notify UI
		await NotifyJobStatusChangedAsync(job.Id, newStatus.ToString());
		if (JobStateMachine.IsTerminalState(newStatus))
		{
			await NotifyJobCompletedAsync(job.Id, newStatus == JobStatus.Completed, errorMessage);
		}
	}

	/// <summary>
	/// Attempts to fetch a session summary from the provider for pre-populating commit messages.
	/// Falls back to generating a summary from git diff and goal prompt if provider summary unavailable.
	/// </summary>
	private async Task TryFetchSessionSummaryAsync(Job job, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
	{
		// First, try to generate a summary from git diff (no AI call required)
		var diffBasedSummary = JobSummaryGenerator.GenerateSummary(job);

		if (job.ProviderId == Guid.Empty)
		{
			// No provider, use diff-based summary if available
			if (!string.IsNullOrWhiteSpace(diffBasedSummary))
			{
				job.SessionSummary = diffBasedSummary;
				_logger.LogInformation("Generated diff-based summary for job {JobId}: {Summary}",
					job.Id, diffBasedSummary.Length > 100 ? diffBasedSummary[..100] + "..." : diffBasedSummary);
			}
			else
			{
				_logger.LogDebug("Job {JobId} has no provider ID and no diff data for summary", job.Id);
			}
			return;
		}

		try
		{
			using var scope = _scopeFactory.CreateScope();
			var providerService = scope.ServiceProvider.GetRequiredService<IProviderService>();

			var workingDirectory = job.Project?.WorkingPath;
			var sessionSummary = await providerService.GetSessionSummaryAsync(
				job.ProviderId,
				job.SessionId,
				workingDirectory,
				job.Output ?? job.ConsoleOutput, // Use Output or ConsoleOutput as fallback
				cancellationToken);

			if (sessionSummary.Success && !string.IsNullOrWhiteSpace(sessionSummary.Summary))
			{
				job.SessionSummary = sessionSummary.Summary;
				_logger.LogInformation("Retrieved session summary for job {JobId} from {Source}: {Summary}",
					job.Id, sessionSummary.Source, sessionSummary.Summary.Length > 100
						? sessionSummary.Summary[..100] + "..."
						: sessionSummary.Summary);
			}
			else if (!string.IsNullOrWhiteSpace(diffBasedSummary))
			{
				// Fallback to diff-based summary when provider summary unavailable
				job.SessionSummary = diffBasedSummary;
				_logger.LogInformation("Using diff-based summary for job {JobId} (provider summary unavailable): {Summary}",
					job.Id, diffBasedSummary.Length > 100 ? diffBasedSummary[..100] + "..." : diffBasedSummary);
			}
			else
			{
				_logger.LogDebug("Could not retrieve session summary for job {JobId}: {Error}",
					job.Id, sessionSummary.ErrorMessage ?? "No summary available");
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to fetch session summary for job {JobId}", job.Id);

			// Use diff-based summary as fallback on error
			if (!string.IsNullOrWhiteSpace(diffBasedSummary))
			{
				job.SessionSummary = diffBasedSummary;
				_logger.LogInformation("Using diff-based summary for job {JobId} after provider error: {Summary}",
					job.Id, diffBasedSummary.Length > 100 ? diffBasedSummary[..100] + "..." : diffBasedSummary);
			}
		}
	}

	/// <summary>
	/// Checks for jobs that were waiting on dependencies and can now run
	/// </summary>
	private async Task CheckDependentJobsAsync(CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		// Find jobs with dependencies that might now be satisfied
		var waitingJobs = await dbContext.Jobs
			.Where(j => j.Status == JobStatus.Pending && j.DependsOnJobId.HasValue)
			.ToListAsync(cancellationToken);

		foreach (var job in waitingJobs)
		{
			var dependency = await dbContext.Jobs.FindAsync(new object[] { job.DependsOnJobId!.Value }, cancellationToken);
			if (dependency == null)
			{
				// Dependency doesn't exist, move to ready
				job.Status = JobStatus.New;
				job.DependsOnJobId = null;
				_logger.LogWarning("Job {JobId} dependency {DependencyId} not found, moving to ready",
					job.Id, job.DependsOnJobId);
			}
			else if (dependency.Status == JobStatus.Completed)
			{
				// Dependency completed successfully, move to ready
				job.Status = JobStatus.New;
				_logger.LogInformation("Job {JobId} dependency {DependencyId} completed, moving to ready",
					job.Id, dependency.Id);
			}
			else if (dependency.Status == JobStatus.Failed || dependency.Status == JobStatus.Cancelled)
			{
				// Dependency failed, fail this job too
				job.Status = JobStatus.Failed;
				job.CompletedAt = DateTime.UtcNow;
				job.ErrorMessage = $"Dependency job {dependency.Id} {dependency.Status.ToString().ToLower()}";
				_logger.LogWarning("Job {JobId} failed due to dependency {DependencyId} {Status}",
					job.Id, dependency.Id, dependency.Status);

				await NotifyJobCompletedAsync(job.Id, false, job.ErrorMessage);
			}
		}

		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private void OnProcessUnhealthy(Guid jobId, ProcessHealthStatus status)
	{
		_logger.LogWarning("Process for job {JobId} unhealthy: {Reason}", jobId, status.Reason);

		// Mark job for evaluation on next check cycle
		Task.Run(async () =>
		{
			try
			{
				using var scope = _scopeFactory.CreateScope();
				var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
				var job = await dbContext.Jobs.FindAsync(jobId);
				if (job != null && JobStateMachine.IsActiveState(job.Status))
				{
					// Update stall detection timestamp
					job.CurrentActivity = $"Unhealthy: {status.Reason}";
					await dbContext.SaveChangesAsync();
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error handling unhealthy process for job {JobId}", jobId);
			}
		});
	}

	private void OnProcessExitedUnexpectedly(Guid jobId, int exitCode)
	{
		_logger.LogWarning("Process for job {JobId} exited unexpectedly with code {ExitCode}", jobId, exitCode);

		Task.Run(async () =>
		{
			try
			{
				using var scope = _scopeFactory.CreateScope();
				var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
				var job = await dbContext.Jobs.FindAsync(jobId);

				if (job != null && JobStateMachine.IsActiveState(job.Status))
				{
					if (exitCode != 0)
					{
						// Process failed
						if (job.RetryCount < job.MaxRetries)
						{
							job.Status = JobStatus.New;
							job.RetryCount++;
							job.StartedAt = null;
							job.WorkerInstanceId = null;
							job.ProcessId = null;
							job.ErrorMessage = $"Process exited with code {exitCode}, retry {job.RetryCount}/{job.MaxRetries}";
						}
						else
						{
							job.Status = JobStatus.Failed;
							job.CompletedAt = DateTime.UtcNow;
							job.ErrorMessage = $"Process exited with code {exitCode} after {job.RetryCount} retries";
						}
					}
					else
					{
						// Process completed successfully
						job.Status = JobStatus.Completed;
						job.CompletedAt = DateTime.UtcNow;
					}

					job.WorkerInstanceId = null;
					job.ProcessId = null;
					job.CurrentActivity = null;

					await dbContext.SaveChangesAsync();
					await NotifyJobStatusChangedAsync(job.Id, job.Status.ToString());

					if (JobStateMachine.IsTerminalState(job.Status))
					{
						await NotifyJobCompletedAsync(job.Id, job.Status == JobStatus.Completed, job.ErrorMessage);
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error handling unexpected process exit for job {JobId}", jobId);
			}
		});
	}

	private async Task NotifyJobStatusChangedAsync(Guid jobId, string status)
	{
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyJobStatusChanged(jobId, status);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to notify status change for job {JobId}", jobId);
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
				_logger.LogWarning(ex, "Failed to notify completion for job {JobId}", jobId);
			}
		}
	}

	/// <summary>
	/// Handles any process exit (success or failure) to ensure UI is updated immediately
	/// </summary>
	private void OnProcessExited(Guid jobId, int exitCode)
	{
		_logger.LogInformation("Process for job {JobId} exited with code {ExitCode}", jobId, exitCode);

		Task.Run(async () =>
		{
			try
			{
				using var scope = _scopeFactory.CreateScope();
				var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
				var job = await dbContext.Jobs
					.Include(j => j.Project)
					.Include(j => j.Provider)
					.FirstOrDefaultAsync(j => j.Id == jobId);

				if (job != null && JobStateMachine.IsActiveState(job.Status))
				{
					if (exitCode == 0)
					{
						// Process completed successfully
						job.Status = JobStatus.Completed;
						job.CompletedAt = DateTime.UtcNow;
						job.CurrentActivity = null;
						job.WorkerInstanceId = null;
						job.ProcessId = null;

						_logger.LogInformation("Job {JobId} completed successfully", jobId);

						// Try to get a session summary for pre-populating commit messages
						await TryFetchSessionSummaryAsync(job, dbContext, CancellationToken.None);

						// Update provider health
						if (job.ProviderId != Guid.Empty)
						{
							_healthTracker.RecordSuccess(job.ProviderId,
								job.StartedAt.HasValue ? DateTime.UtcNow - job.StartedAt.Value : null);
							_healthTracker.DecrementProviderLoad(job.ProviderId);
						}

						await dbContext.SaveChangesAsync();

						// Notify UI immediately
						await NotifyJobStatusChangedAsync(job.Id, job.Status.ToString());
						await NotifyJobCompletedAsync(job.Id, true, null);
					}
					// Non-zero exit codes are handled by OnProcessExitedUnexpectedly
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error handling process exit for job {JobId}", jobId);
			}
		});
	}

	public override void Dispose()
	{
		_processSupervisor.ProcessUnhealthy -= OnProcessUnhealthy;
		_processSupervisor.ProcessExitedUnexpectedly -= OnProcessExitedUnexpectedly;
		_processSupervisor.ProcessExited -= OnProcessExited;
		base.Dispose();
	}
}
