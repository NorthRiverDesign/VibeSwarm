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
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(5);

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
        _logger.LogInformation("Job Processing Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing jobs");
            }

            await Task.Delay(_pollingInterval, stoppingToken);
        }

        _logger.LogInformation("Job Processing Service stopped");
    }

    private async Task ProcessPendingJobsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobService = scope.ServiceProvider.GetRequiredService<IJobService>();
        var providerService = scope.ServiceProvider.GetRequiredService<IProviderService>();

        var pendingJobs = await jobService.GetPendingJobsAsync(stoppingToken);

        foreach (var job in pendingJobs)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            // Process each job in a separate task but wait for it to complete
            // This ensures we don't overload the system with too many concurrent jobs
            await ProcessJobAsync(job, jobService, providerService, stoppingToken);
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

            // Create a cancellation token that combines the stopping token with job-specific cancellation
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            // Start a background task to monitor for cancellation requests
            var cancellationMonitorTask = MonitorCancellationAsync(job.Id, jobService, linkedCts, stoppingToken);

            // Execute the job with session support
            var workingDirectory = job.Project?.WorkingPath;

            // Track last progress update time to avoid excessive database writes
            var lastProgressUpdate = DateTime.MinValue;
            var progressUpdateInterval = TimeSpan.FromSeconds(2);

            var progress = new Progress<ExecutionProgress>(async p =>
            {
                var activity = !string.IsNullOrEmpty(p.ToolName)
                    ? $"Running tool: {p.ToolName}"
                    : (p.IsStreaming ? "Processing..." : p.CurrentMessage ?? "Working...");

                _logger.LogDebug("Job {JobId} progress: {Activity}", job.Id, activity);

                // Throttle progress updates to the database
                if (DateTime.UtcNow - lastProgressUpdate >= progressUpdateInterval)
                {
                    lastProgressUpdate = DateTime.UtcNow;
                    var timestamp = DateTime.UtcNow;
                    try
                    {
                        await jobService.UpdateProgressAsync(job.Id, activity, CancellationToken.None);
                        await NotifyJobActivityAsync(job.Id, activity, timestamp);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update progress for job {JobId}", job.Id);
                    }
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
