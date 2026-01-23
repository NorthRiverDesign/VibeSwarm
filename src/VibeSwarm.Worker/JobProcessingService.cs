using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Worker;

public class JobProcessingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobProcessingService> _logger;
    private readonly IJobUpdateService? _jobUpdateService;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(1); // Fast polling for immediate job pickup
    private readonly int _maxConcurrentJobs = 5; // Maximum number of concurrent jobs
    private readonly Dictionary<Guid, JobExecutionContext> _runningJobs = new();
    private readonly SemaphoreSlim _jobsLock = new(1, 1);
    private readonly SemaphoreSlim _processingTrigger = new(0); // Semaphore to trigger immediate processing
    private static readonly string _workerInstanceId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <summary>
    /// Gets the unique identifier for this worker instance
    /// </summary>
    public static string GetWorkerInstanceId() => _workerInstanceId;

    public JobProcessingService(
        IServiceScopeFactory scopeFactory,
        ILogger<JobProcessingService> logger,
        IJobUpdateService? jobUpdateService = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _jobUpdateService = jobUpdateService;
    }

    /// <summary>
    /// Context for tracking job execution including cancellation and process info
    /// </summary>
    private class JobExecutionContext
    {
        public Task Task { get; set; } = null!;
        public CancellationTokenSource? CancellationTokenSource { get; set; }
        public int? ProcessId { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job Processing Service started (Worker: {WorkerId}, Max concurrent jobs: {MaxJobs})",
            _workerInstanceId, _maxConcurrentJobs);

        // Recover any orphaned jobs from previous runs on startup
        await RecoverOrphanedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
                await CleanupCompletedJobsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing jobs");
            }

            // Wait for either the polling interval or a trigger signal
            try
            {
                await _processingTrigger.WaitAsync(_pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        // Cancel all running jobs on shutdown
        _logger.LogInformation("Cancelling {Count} running jobs for shutdown...", _runningJobs.Count);
        await _jobsLock.WaitAsync();
        try
        {
            foreach (var context in _runningJobs.Values)
            {
                context.CancellationTokenSource?.Cancel();
            }
        }
        finally
        {
            _jobsLock.Release();
        }

        // Wait for all running jobs to complete on shutdown
        _logger.LogInformation("Waiting for {Count} running jobs to complete...", _runningJobs.Count);
        var tasks = _runningJobs.Values.Select(c => c.Task).ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks);
        }

        _logger.LogInformation("Job Processing Service stopped");
    }

    /// <summary>
    /// Recovers jobs that were left in Started/Processing state by this worker in a previous run.
    /// </summary>
    private async Task RecoverOrphanedJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

            // Find jobs that were being processed by any worker but appear orphaned
            // (Started/Processing with old heartbeats)
            var cutoffTime = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            var orphanedJobs = await dbContext.Jobs
                .Where(j => (j.Status == JobStatus.Started || j.Status == JobStatus.Processing))
                .Where(j => !j.LastHeartbeatAt.HasValue || j.LastHeartbeatAt.Value < cutoffTime)
                .ToListAsync(cancellationToken);

            foreach (var job in orphanedJobs)
            {
                _logger.LogWarning("Found orphaned job {JobId} from worker {WorkerId}, resetting for retry",
                    job.Id, job.WorkerInstanceId ?? "unknown");

                if (job.MaxRetries == 0 || job.RetryCount < job.MaxRetries)
                {
                    job.Status = JobStatus.New;
                    job.RetryCount++;
                    job.StartedAt = null;
                    job.WorkerInstanceId = null;
                    job.LastHeartbeatAt = null;
                    job.ProcessId = null;
                    job.CurrentActivity = null;
                    job.ErrorMessage = "Worker crashed or became unresponsive. Automatic recovery.";
                }
                else
                {
                    job.Status = JobStatus.Failed;
                    job.CompletedAt = DateTime.UtcNow;
                    job.WorkerInstanceId = null;
                    job.ProcessId = null;
                    job.CurrentActivity = null;
                    job.ErrorMessage = $"Job failed after {job.RetryCount} retry attempts (worker crash).";
                }
            }

            if (orphanedJobs.Any())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Recovered {Count} orphaned jobs", orphanedJobs.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recovering orphaned jobs");
        }
    }

    /// <summary>
    /// Triggers immediate job processing (called when a new job is created)
    /// </summary>
    public void TriggerProcessing()
    {
        try
        {
            _processingTrigger.Release();
        }
        catch
        {
            // Ignore if semaphore is already signaled
        }
    }

    private async Task ProcessPendingJobsAsync(CancellationToken stoppingToken)
    {
        await _jobsLock.WaitAsync(stoppingToken);
        try
        {
            // Check how many slots are available
            var availableSlots = _maxConcurrentJobs - _runningJobs.Count;
            if (availableSlots <= 0)
            {
                return; // All slots are filled
            }

            // Get pending jobs
            using var scope = _scopeFactory.CreateScope();
            var jobService = scope.ServiceProvider.GetRequiredService<IJobService>();
            var pendingJobs = (await jobService.GetPendingJobsAsync(stoppingToken)).ToList();

            if (!pendingJobs.Any())
            {
                return;
            }

            _logger.LogInformation("Found {PendingCount} pending jobs, {AvailableSlots} slots available, {RunningCount} jobs running",
                pendingJobs.Count, availableSlots, _runningJobs.Count);

            // Start new jobs up to the available slots
            var jobsToStart = pendingJobs.Take(availableSlots);
            foreach (var job in jobsToStart)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                // Create a linked cancellation token for this job
                var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var context = new JobExecutionContext { CancellationTokenSource = jobCts };

                // Start job processing in background
                context.Task = Task.Run(async () =>
                {
                    using var jobScope = _scopeFactory.CreateScope();
                    var scopedJobService = jobScope.ServiceProvider.GetRequiredService<IJobService>();
                    var scopedProviderService = jobScope.ServiceProvider.GetRequiredService<IProviderService>();
                    var scopedDbContext = jobScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

                    await ProcessJobAsync(job, scopedJobService, scopedProviderService, scopedDbContext, context, jobCts.Token);
                }, stoppingToken);

                _runningJobs[job.Id] = context;
                _logger.LogInformation("Started processing job {JobId} ({RunningCount}/{MaxConcurrent} slots used)",
                    job.Id, _runningJobs.Count, _maxConcurrentJobs);
            }
        }
        finally
        {
            _jobsLock.Release();
        }
    }

    private async Task CleanupCompletedJobsAsync()
    {
        await _jobsLock.WaitAsync();
        try
        {
            var completedJobs = _runningJobs.Where(kvp => kvp.Value.Task.IsCompleted).ToList();
            foreach (var kvp in completedJobs)
            {
                _runningJobs.Remove(kvp.Key);
                kvp.Value.CancellationTokenSource?.Dispose();
                _logger.LogDebug("Removed completed job {JobId} from running jobs tracking", kvp.Key);

                // Check for exceptions
                if (kvp.Value.Task.IsFaulted && kvp.Value.Task.Exception != null)
                {
                    _logger.LogError(kvp.Value.Task.Exception, "Job {JobId} faulted during execution", kvp.Key);
                }
            }
        }
        finally
        {
            _jobsLock.Release();
        }
    }

    private async Task ProcessJobAsync(
        Job job,
        IJobService jobService,
        IProviderService providerService,
        VibeSwarmDbContext dbContext,
        JobExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing job {JobId} for project {ProjectName} (Worker: {WorkerId})",
            job.Id, job.Project?.Name, _workerInstanceId);

        try
        {
            // Check if job was cancelled before we even started
            if (await jobService.IsCancellationRequestedAsync(job.Id, cancellationToken))
            {
                await jobService.UpdateStatusAsync(job.Id, JobStatus.Cancelled,
                    errorMessage: "Job was cancelled before processing started", cancellationToken: cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled before processing started");
                return;
            }

            // Mark job as started and claim ownership
            await ClaimJobAsync(job.Id, dbContext, cancellationToken);
            await NotifyStatusChangedAsync(job.Id, JobStatus.Started);

            // Check again after status update - double-check for race conditions
            if (await jobService.IsCancellationRequestedAsync(job.Id, cancellationToken))
            {
                await ReleaseJobAsync(job.Id, JobStatus.Cancelled, "Job was cancelled", dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled");
                return;
            }

            // Check if provider is available
            if (job.Provider == null)
            {
                await ReleaseJobAsync(job.Id, JobStatus.Failed, "Provider not found", dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, "Provider not found");
                return;
            }

            if (!job.Provider.IsEnabled)
            {
                await ReleaseJobAsync(job.Id, JobStatus.Failed, "Provider is disabled", dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, "Provider is disabled");
                return;
            }

            // Create provider instance
            var provider = CreateProviderInstance(job.Provider);

            // Test connection and check availability
            var isConnected = await provider.TestConnectionAsync(cancellationToken);
            if (!isConnected)
            {
                await ReleaseJobAsync(job.Id, JobStatus.Failed, "Could not connect to provider", dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, "Could not connect to provider");
                return;
            }

            // Check provider availability (usage limits, etc.)
            var providerInfo = await provider.GetProviderInfoAsync(cancellationToken);
            if (providerInfo.AdditionalInfo.TryGetValue("isAvailable", out var isAvailableObj) && isAvailableObj is bool isAvailable && !isAvailable)
            {
                var reason = providerInfo.AdditionalInfo.TryGetValue("unavailableReason", out var reasonObj)
                    ? reasonObj?.ToString() ?? "Provider not available"
                    : "Provider not available";
                await ReleaseJobAsync(job.Id, JobStatus.Failed, reason, dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, reason);
                return;
            }

            // Update status to processing
            await UpdateJobStatusAsync(job.Id, JobStatus.Processing, dbContext, cancellationToken);
            await NotifyStatusChangedAsync(job.Id, JobStatus.Processing);

            // Send initial activity notification
            var initialActivity = "Initializing coding agent...";
            await UpdateHeartbeatAsync(job.Id, initialActivity, dbContext, cancellationToken);
            await NotifyJobActivityAsync(job.Id, initialActivity, DateTime.UtcNow);

            // Start a background task to monitor for cancellation requests and send heartbeats
            var cancellationMonitorTask = MonitorCancellationAndHeartbeatAsync(job.Id, dbContext, executionContext, cancellationToken);

            // Execute the job with session support
            var workingDirectory = job.Project?.WorkingPath;

            // Track last progress update time to avoid excessive database writes
            var lastProgressUpdate = DateTime.MinValue;
            var progressUpdateInterval = TimeSpan.FromMilliseconds(500); // Update every 0.5 seconds for near real-time feedback

            // Progress<T> doesn't properly handle async callbacks, so we use a synchronous handler
            // that fires updates in the background with proper scoping to avoid DbContext disposal issues
            var progress = new Progress<ExecutionProgress>(p =>
            {
                var activity = !string.IsNullOrEmpty(p.ToolName)
                    ? $"Running tool: {p.ToolName}"
                    : (p.IsStreaming ? "Processing..." : p.CurrentMessage ?? "Working...");

                _logger.LogInformation("Job {JobId} progress: {Activity}", job.Id, activity);

                // Throttle progress updates to avoid database overload
                var now = DateTime.UtcNow;
                if (now - lastProgressUpdate >= progressUpdateInterval)
                {
                    lastProgressUpdate = now;

                    // Fire async updates in the background with a NEW scope to avoid DbContext disposal
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Create a new scope for this background operation
                            using var progressScope = _scopeFactory.CreateScope();
                            var scopedDbContext = progressScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

                            await UpdateHeartbeatAsync(job.Id, activity, scopedDbContext, CancellationToken.None);
                            await NotifyJobActivityAsync(job.Id, activity, now);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to update progress for job {JobId}", job.Id);
                        }
                    });
                }
            });

            var result = await provider.ExecuteWithSessionAsync(
                job.GoalPrompt,
                job.SessionId,
                workingDirectory,
                progress,
                cancellationToken);

            // Stop monitoring cancellation
            executionContext.CancellationTokenSource?.Cancel();
            try { await cancellationMonitorTask; } catch { }

            // Re-fetch job state from database to check cancellation
            using var checkScope = _scopeFactory.CreateScope();
            var checkJobService = checkScope.ServiceProvider.GetRequiredService<IJobService>();
            var wasCancelled = await checkJobService.IsCancellationRequestedAsync(job.Id, CancellationToken.None);

            if (wasCancelled)
            {
                await CompleteJobAsync(job.Id, JobStatus.Cancelled, result.SessionId, result.Output,
                    "Job was cancelled by user", result.InputTokens, result.OutputTokens, result.CostUsd,
                    dbContext, CancellationToken.None);
                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled by user");

                _logger.LogInformation("Job {JobId} was cancelled during execution", job.Id);
            }
            else if (result.Success)
            {
                // Save messages
                if (result.Messages.Count > 0)
                {
                    var messages = result.Messages.Select(m => new JobMessage
                    {
                        Role = ParseMessageRole(m.Role),
                        Content = m.Content,
                        ToolName = m.ToolName,
                        ToolInput = m.ToolInput,
                        ToolOutput = m.ToolOutput,
                        CreatedAt = m.Timestamp
                    });

                    await checkJobService.AddMessagesAsync(job.Id, messages, CancellationToken.None);
                    await NotifyJobMessageAddedAsync(job.Id);
                }

                await CompleteJobAsync(job.Id, JobStatus.Completed, result.SessionId, result.Output,
                    null, result.InputTokens, result.OutputTokens, result.CostUsd,
                    dbContext, CancellationToken.None);

                _logger.LogInformation("Job {JobId} completed successfully. Session: {SessionId}",
                    job.Id, result.SessionId);
                await NotifyJobCompletedAsync(job.Id, true);
            }
            else
            {
                await CompleteJobAsync(job.Id, JobStatus.Failed, result.SessionId, result.Output,
                    result.ErrorMessage, result.InputTokens, result.OutputTokens, result.CostUsd,
                    dbContext, CancellationToken.None);

                _logger.LogWarning("Job {JobId} failed: {Error}", job.Id, result.ErrorMessage);
                await NotifyJobCompletedAsync(job.Id, false, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Job {JobId} was cancelled, resetting for potential retry", job.Id);
            try
            {
                using var resetScope = _scopeFactory.CreateScope();
                var resetDbContext = resetScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                var jobEntity = await resetDbContext.Jobs.FindAsync(job.Id);
                if (jobEntity != null)
                {
                    if (jobEntity.CancellationRequested)
                    {
                        // User requested cancellation
                        jobEntity.Status = JobStatus.Cancelled;
                        jobEntity.CompletedAt = DateTime.UtcNow;
                        jobEntity.ErrorMessage = "Job was cancelled by user";
                    }
                    else
                    {
                        // Service shutdown or timeout - reset for retry
                        jobEntity.Status = JobStatus.New;
                        jobEntity.StartedAt = null;
                        jobEntity.ErrorMessage = "Service shutdown during execution. Queued for retry.";
                    }
                    jobEntity.WorkerInstanceId = null;
                    jobEntity.LastHeartbeatAt = null;
                    jobEntity.ProcessId = null;
                    jobEntity.CurrentActivity = null;
                    await resetDbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception resetEx)
            {
                _logger.LogError(resetEx, "Failed to reset job {JobId} after cancellation", job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}", job.Id);
            try
            {
                using var errorScope = _scopeFactory.CreateScope();
                var errorDbContext = errorScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                await ReleaseJobAsync(job.Id, JobStatus.Failed, ex.Message, errorDbContext, CancellationToken.None);
            }
            catch { }
            await NotifyJobCompletedAsync(job.Id, false, ex.Message);
        }
    }

    /// <summary>
    /// Claims ownership of a job by this worker instance
    /// </summary>
    private async Task ClaimJobAsync(Guid jobId, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            job.Status = JobStatus.Started;
            job.StartedAt = DateTime.UtcNow;
            job.WorkerInstanceId = _workerInstanceId;
            job.LastHeartbeatAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Releases ownership of a job and sets final status
    /// </summary>
    private async Task ReleaseJobAsync(Guid jobId, JobStatus status, string? errorMessage, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            job.Status = status;
            job.CompletedAt = DateTime.UtcNow;
            job.WorkerInstanceId = null;
            job.LastHeartbeatAt = null;
            job.ProcessId = null;
            job.CurrentActivity = null;
            job.ErrorMessage = errorMessage;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Updates job status
    /// </summary>
    private async Task UpdateJobStatusAsync(Guid jobId, JobStatus status, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            job.Status = status;
            job.LastHeartbeatAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Updates heartbeat and current activity
    /// </summary>
    private async Task UpdateHeartbeatAsync(Guid jobId, string? activity, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            job.LastHeartbeatAt = DateTime.UtcNow;
            job.LastActivityAt = DateTime.UtcNow;
            job.CurrentActivity = activity;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Completes a job with full result data
    /// </summary>
    private async Task CompleteJobAsync(
        Guid jobId, JobStatus status, string? sessionId, string? output, string? errorMessage,
        int? inputTokens, int? outputTokens, decimal? costUsd,
        VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            job.Status = status;
            job.CompletedAt = DateTime.UtcNow;
            job.SessionId = sessionId ?? job.SessionId;
            job.Output = output ?? job.Output;
            job.ErrorMessage = errorMessage;
            job.InputTokens = inputTokens ?? job.InputTokens;
            job.OutputTokens = outputTokens ?? job.OutputTokens;
            job.TotalCostUsd = costUsd ?? job.TotalCostUsd;
            job.WorkerInstanceId = null;
            job.LastHeartbeatAt = null;
            job.ProcessId = null;
            job.CurrentActivity = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Monitors for cancellation requests and sends regular heartbeats
    /// </summary>
    private async Task MonitorCancellationAndHeartbeatAsync(
        Guid jobId,
        VibeSwarmDbContext dbContext,
        JobExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var heartbeatInterval = TimeSpan.FromSeconds(10);
        var cancellationCheckInterval = TimeSpan.FromSeconds(2);
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Check for cancellation request
                using var checkScope = _scopeFactory.CreateScope();
                var checkDbContext = checkScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                var job = await checkDbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

                if (job?.CancellationRequested == true)
                {
                    _logger.LogInformation("Cancellation requested for job {JobId}", jobId);
                    executionContext.CancellationTokenSource?.Cancel();
                    break;
                }

                // Send heartbeat periodically
                var now = DateTime.UtcNow;
                if (now - lastHeartbeat >= heartbeatInterval)
                {
                    lastHeartbeat = now;
                    using var heartbeatScope = _scopeFactory.CreateScope();
                    var heartbeatDbContext = heartbeatScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                    var heartbeatJob = await heartbeatDbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
                    if (heartbeatJob != null)
                    {
                        heartbeatJob.LastHeartbeatAt = now;
                        await heartbeatDbContext.SaveChangesAsync(cancellationToken);
                    }
                    _logger.LogDebug("Sent heartbeat for job {JobId}", jobId);
                }

                await Task.Delay(cancellationCheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in cancellation/heartbeat monitor for job {JobId}", jobId);
        }
    }

    private static IProvider CreateProviderInstance(Provider config)
    {
        return config.Type switch
        {
            ProviderType.OpenCode => new OpenCodeProvider(config),
            ProviderType.Claude => new ClaudeProvider(config),
            _ => throw new NotSupportedException($"Provider type {config.Type} is not supported.")
        };
    }

    private static MessageRole ParseMessageRole(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "user" => MessageRole.User,
            "assistant" => MessageRole.Assistant,
            "system" => MessageRole.System,
            "tool_use" => MessageRole.ToolUse,
            "tool_result" => MessageRole.ToolResult,
            _ => MessageRole.Assistant
        };
    }

    private async Task NotifyStatusChangedAsync(Guid jobId, JobStatus status)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(jobId, status.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send status change notification for job {JobId}", jobId);
            }
        }
    }

    private async Task NotifyJobActivityAsync(Guid jobId, string activity, DateTime timestamp)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobActivity(jobId, activity, timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send activity notification for job {JobId}", jobId);
            }
        }
    }

    private async Task NotifyJobMessageAddedAsync(Guid jobId)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobMessageAdded(jobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send message added notification for job {JobId}", jobId);
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
                _logger.LogWarning(ex, "Failed to send completion notification for job {JobId}", jobId);
            }
        }
    }
}
