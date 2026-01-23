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
    private readonly Dictionary<Guid, Task> _runningJobs = new();
    private readonly SemaphoreSlim _jobsLock = new(1, 1);
    private readonly SemaphoreSlim _processingTrigger = new(0); // Semaphore to trigger immediate processing

    public JobProcessingService(
        IServiceScopeFactory scopeFactory,
        ILogger<JobProcessingService> logger,
        IJobUpdateService? jobUpdateService = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _jobUpdateService = jobUpdateService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job Processing Service started (Max concurrent jobs: {MaxJobs})", _maxConcurrentJobs);

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

        // Wait for all running jobs to complete on shutdown
        _logger.LogInformation("Waiting for {Count} running jobs to complete...", _runningJobs.Count);
        await Task.WhenAll(_runningJobs.Values.ToArray());

        _logger.LogInformation("Job Processing Service stopped");
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

                // Start job processing in background
                var jobTask = Task.Run(async () =>
                {
                    using var jobScope = _scopeFactory.CreateScope();
                    var scopedJobService = jobScope.ServiceProvider.GetRequiredService<IJobService>();
                    var scopedProviderService = jobScope.ServiceProvider.GetRequiredService<IProviderService>();

                    await ProcessJobAsync(job, scopedJobService, scopedProviderService, stoppingToken);
                }, stoppingToken);

                _runningJobs[job.Id] = jobTask;
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
            var completedJobs = _runningJobs.Where(kvp => kvp.Value.IsCompleted).ToList();
            foreach (var kvp in completedJobs)
            {
                _runningJobs.Remove(kvp.Key);
                _logger.LogDebug("Removed completed job {JobId} from running jobs tracking", kvp.Key);

                // Check for exceptions
                if (kvp.Value.IsFaulted && kvp.Value.Exception != null)
                {
                    _logger.LogError(kvp.Value.Exception, "Job {JobId} faulted during execution", kvp.Key);
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
        CancellationToken stoppingToken)
    {
        _logger.LogInformation("Processing job {JobId} for project {ProjectName}",
            job.Id, job.Project?.Name);

        try
        {
            // Check if job was cancelled before we even started
            if (await jobService.IsCancellationRequestedAsync(job.Id, stoppingToken))
            {
                await jobService.UpdateStatusAsync(job.Id, JobStatus.Cancelled,
                    errorMessage: "Job was cancelled before processing started", cancellationToken: stoppingToken);
                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled before processing started");
                return;
            }

            // Mark job as started
            await jobService.UpdateStatusAsync(job.Id, JobStatus.Started, cancellationToken: stoppingToken);
            await NotifyStatusChangedAsync(job.Id, JobStatus.Started);

            // Check again after status update - double-check for race conditions
            if (await jobService.IsCancellationRequestedAsync(job.Id, stoppingToken))
            {
                await jobService.UpdateStatusAsync(job.Id, JobStatus.Cancelled,
                    errorMessage: "Job was cancelled", cancellationToken: stoppingToken);
                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled");
                return;
            }

            // Check if provider is available
            if (job.Provider == null)
            {
                await jobService.UpdateStatusAsync(job.Id, JobStatus.Failed,
                    errorMessage: "Provider not found", cancellationToken: stoppingToken);
                await NotifyJobCompletedAsync(job.Id, false, "Provider not found");
                return;
            }

            if (!job.Provider.IsEnabled)
            {
                await jobService.UpdateStatusAsync(job.Id, JobStatus.Failed,
                    errorMessage: "Provider is disabled", cancellationToken: stoppingToken);
                await NotifyJobCompletedAsync(job.Id, false, "Provider is disabled");
                return;
            }

            // Create provider instance
            var provider = CreateProviderInstance(job.Provider);

            // Test connection
            var isConnected = await provider.TestConnectionAsync(stoppingToken);
            if (!isConnected)
            {
                await jobService.UpdateStatusAsync(job.Id, JobStatus.Failed,
                    errorMessage: "Could not connect to provider", cancellationToken: stoppingToken);
                await NotifyJobCompletedAsync(job.Id, false, "Could not connect to provider");
                return;
            }

            // Update status to processing
            await jobService.UpdateStatusAsync(job.Id, JobStatus.Processing, cancellationToken: stoppingToken);
            await NotifyStatusChangedAsync(job.Id, JobStatus.Processing);

            // Send initial activity notification
            var initialActivity = "Initializing coding agent...";
            await jobService.UpdateProgressAsync(job.Id, initialActivity, stoppingToken);
            await NotifyJobActivityAsync(job.Id, initialActivity, DateTime.UtcNow);

            // Create a cancellation token that combines the stopping token with job-specific cancellation
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            // Start a background task to monitor for cancellation requests
            var cancellationMonitorTask = MonitorCancellationAsync(job.Id, jobService, linkedCts, stoppingToken);

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
                            var scopedJobService = progressScope.ServiceProvider.GetRequiredService<IJobService>();

                            await scopedJobService.UpdateProgressAsync(job.Id, activity, CancellationToken.None);
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
                linkedCts.Token);

            // Stop monitoring cancellation
            linkedCts.Cancel();
            try { await cancellationMonitorTask; } catch { }

            // Check if job was cancelled - do this check first to ensure we don't override cancellation status
            var wasCancelled = await jobService.IsCancellationRequestedAsync(job.Id, stoppingToken);

            if (wasCancelled)
            {
                await jobService.UpdateJobResultAsync(
                    job.Id,
                    JobStatus.Cancelled,
                    result.SessionId,
                    result.Output,
                    "Job was cancelled by user",
                    result.InputTokens,
                    result.OutputTokens,
                    result.CostUsd,
                    stoppingToken);
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

                    await jobService.AddMessagesAsync(job.Id, messages, stoppingToken);
                    await NotifyJobMessageAddedAsync(job.Id);
                }

                await jobService.UpdateJobResultAsync(
                    job.Id,
                    JobStatus.Completed,
                    result.SessionId,
                    result.Output,
                    null,
                    result.InputTokens,
                    result.OutputTokens,
                    result.CostUsd,
                    stoppingToken);

                _logger.LogInformation("Job {JobId} completed successfully. Session: {SessionId}",
                    job.Id, result.SessionId);
                await NotifyJobCompletedAsync(job.Id, true);
            }
            else
            {
                await jobService.UpdateJobResultAsync(
                    job.Id,
                    JobStatus.Failed,
                    result.SessionId,
                    result.Output,
                    result.ErrorMessage,
                    result.InputTokens,
                    result.OutputTokens,
                    result.CostUsd,
                    stoppingToken);

                _logger.LogWarning("Job {JobId} failed: {Error}", job.Id, result.ErrorMessage);
                await NotifyJobCompletedAsync(job.Id, false, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Job {JobId} was interrupted due to service shutdown, resetting to New status", job.Id);
            // Reset job to New status so it can be retried when service restarts
            // Use ResetJobAsync to properly clear all state
            try
            {
                // First mark as Failed so we can reset it
                await jobService.UpdateStatusAsync(job.Id, JobStatus.Failed,
                    errorMessage: "Service shutdown during execution", cancellationToken: CancellationToken.None);
                // Then reset it back to New
                await jobService.ResetJobAsync(job.Id, CancellationToken.None);
            }
            catch
            {
                // If reset fails, at least try to set status back to New
                await jobService.UpdateStatusAsync(job.Id, JobStatus.New, cancellationToken: CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}", job.Id);
            await jobService.UpdateStatusAsync(job.Id, JobStatus.Failed,
                errorMessage: ex.Message, cancellationToken: stoppingToken);
            await NotifyJobCompletedAsync(job.Id, false, ex.Message);
        }
    }

    private async Task MonitorCancellationAsync(
        Guid jobId,
        IJobService jobService,
        CancellationTokenSource linkedCts,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!linkedCts.Token.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                if (await jobService.IsCancellationRequestedAsync(jobId, stoppingToken))
                {
                    _logger.LogInformation("Cancellation requested for job {JobId}", jobId);
                    linkedCts.Cancel();
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the linked CTS is cancelled
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
