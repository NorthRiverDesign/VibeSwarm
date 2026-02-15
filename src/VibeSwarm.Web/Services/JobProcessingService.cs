using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

public class JobProcessingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobProcessingService> _logger;
    private readonly IJobUpdateService? _jobUpdateService;
    private readonly IJobCoordinatorService? _jobCoordinator;
    private readonly IProviderHealthTracker? _healthTracker;
    private readonly IProviderUsageService? _providerUsageService;
    private readonly ProcessSupervisor? _processSupervisor;
    private readonly IVersionControlService _versionControlService;
    private readonly IInteractionResponseService? _interactionResponseService;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(3); // Poll less frequently, SignalR handles real-time updates
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
        IVersionControlService versionControlService,
        IJobUpdateService? jobUpdateService = null,
        IJobCoordinatorService? jobCoordinator = null,
        IProviderHealthTracker? healthTracker = null,
        IProviderUsageService? providerUsageService = null,
        ProcessSupervisor? processSupervisor = null,
        IInteractionResponseService? interactionResponseService = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _versionControlService = versionControlService;
        _jobUpdateService = jobUpdateService;
        _jobCoordinator = jobCoordinator;
        _healthTracker = healthTracker;
        _providerUsageService = providerUsageService;
        _processSupervisor = processSupervisor;
        _interactionResponseService = interactionResponseService;
    }

    /// <summary>
    /// Context for tracking job execution including cancellation and process info
    /// </summary>
    private class JobExecutionContext
    {
        public Task Task { get; set; } = null!;
        public CancellationTokenSource? CancellationTokenSource { get; set; }
        public int? ProcessId { get; set; }
        public Guid ProviderId { get; set; }

        /// <summary>
        /// The provider instance used for this job execution.
        /// Stored for disposal of SDK providers that implement IAsyncDisposable.
        /// </summary>
        public IProvider? ProviderInstance { get; set; }

        /// <summary>
        /// The full CLI command used to execute this job
        /// </summary>
        public string? CommandUsed { get; set; }

        /// <summary>
        /// Accumulates console output during execution for storage in the database
        /// </summary>
        public StringBuilder ConsoleOutputBuffer { get; } = new StringBuilder();

        /// <summary>
        /// Lock object for thread-safe access to ConsoleOutputBuffer
        /// </summary>
        public object OutputLock { get; } = new object();

        /// <summary>
        /// Git commit hash at the start of job execution
        /// </summary>
        public string? GitCommitBefore { get; set; }

        /// <summary>
        /// Tracks recent output lines for interaction detection context
        /// </summary>
        public Queue<string> RecentOutputLines { get; } = new Queue<string>(20);

        /// <summary>
        /// Whether the job is currently paused waiting for user interaction
        /// </summary>
        public bool IsPausedForInteraction { get; set; }

        /// <summary>
        /// Task completion source for waiting on user interaction response
        /// </summary>
        public TaskCompletionSource<string>? InteractionResponseTcs { get; set; }

        /// <summary>
        /// The current interaction request being processed
        /// </summary>
        public InteractionDetector.InteractionRequest? CurrentInteractionRequest { get; set; }

        /// <summary>
        /// Maximum console output size (5 MB)
        /// </summary>
        private const int MaxOutputSize = 5 * 1024 * 1024;

        /// <summary>
        /// Maximum recent lines to keep for context
        /// </summary>
        private const int MaxRecentLines = 20;

        public void AppendOutput(string line, bool isError)
        {
            lock (OutputLock)
            {
                if (ConsoleOutputBuffer.Length < MaxOutputSize)
                {
                    var prefix = isError ? "[ERR] " : "";
                    ConsoleOutputBuffer.AppendLine($"{prefix}{line}");
                }
                else if (!ConsoleOutputBuffer.ToString().EndsWith("[output truncated]"))
                {
                    ConsoleOutputBuffer.AppendLine("\n... [output truncated] ...");
                }

                // Track recent lines for interaction detection
                if (RecentOutputLines.Count >= MaxRecentLines)
                {
                    RecentOutputLines.Dequeue();
                }
                RecentOutputLines.Enqueue(line);
            }
        }

        public string GetConsoleOutput()
        {
            lock (OutputLock)
            {
                return ConsoleOutputBuffer.ToString();
            }
        }

        public IEnumerable<string> GetRecentOutputLines()
        {
            lock (OutputLock)
            {
                return RecentOutputLines.ToArray();
            }
        }
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

                // Release job from provider tracking
                if (_jobCoordinator != null)
                {
                    await _jobCoordinator.ReleaseJobFromProviderAsync(kvp.Key, kvp.Value.ProviderId,
                        kvp.Value.Task.IsCompletedSuccessfully, CancellationToken.None);
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

        // Store provider ID for later cleanup
        executionContext.ProviderId = job.ProviderId;

        try
        {
            // Check if job was cancelled before we even started
            if (await jobService.IsCancellationRequestedAsync(job.Id, cancellationToken))
            {
                var transition = JobStateMachine.TryTransition(job, JobStatus.Cancelled, "Cancelled before start");
                await dbContext.SaveChangesAsync(cancellationToken);
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
            executionContext.ProviderInstance = provider;

            // Preflight health check: Test connection and validate CLI accessibility
            _logger.LogInformation("Running preflight health checks for job {JobId}", job.Id);
            var isConnected = await provider.TestConnectionAsync(cancellationToken);
            if (!isConnected)
            {
                var errorMsg = "Preflight check failed: Could not connect to provider. ";
                if (!string.IsNullOrEmpty(provider.LastConnectionError))
                {
                    errorMsg += provider.LastConnectionError;
                    _logger.LogWarning("Provider connection test failed for job {JobId}: {Error}", job.Id, provider.LastConnectionError);
                }
                else
                {
                    errorMsg += "Ensure the CLI is installed and accessible from the host system.";
                    _logger.LogWarning("Provider connection test failed for job {JobId} with no error details", job.Id);
                }
                await ReleaseJobAsync(job.Id, JobStatus.Failed, errorMsg, dbContext, cancellationToken);
                await NotifyJobCompletedAsync(job.Id, false, errorMsg);
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

            // Check usage exhaustion before execution
            if (_providerUsageService != null)
            {
                var exhaustionWarning = await _providerUsageService.CheckExhaustionAsync(job.ProviderId, cancellationToken: cancellationToken);
                if (exhaustionWarning?.IsExhausted == true)
                {
                    var reason = $"Provider usage limit exhausted: {exhaustionWarning.Message}";
                    _logger.LogWarning("Job {JobId} blocked due to provider usage exhaustion: {Message}", job.Id, exhaustionWarning.Message);
                    await ReleaseJobAsync(job.Id, JobStatus.Failed, reason, dbContext, cancellationToken);
                    await NotifyJobCompletedAsync(job.Id, false, reason);

                    // Also broadcast the warning via SignalR
                    if (_jobUpdateService != null)
                    {
                        await _jobUpdateService.NotifyProviderUsageWarning(
                            job.ProviderId,
                            job.Provider.Name,
                            exhaustionWarning.PercentUsed,
                            exhaustionWarning.Message,
                            exhaustionWarning.IsExhausted,
                            exhaustionWarning.ResetTime);
                    }
                    return;
                }
            }

            // Update status to processing
            await UpdateJobStatusAsync(job.Id, JobStatus.Processing, dbContext, cancellationToken);
            await NotifyStatusChangedAsync(job.Id, JobStatus.Processing);

            // Send initial activity notification
            var initialActivity = "Initializing coding agent...";
            await UpdateHeartbeatAsync(job.Id, initialActivity, dbContext, cancellationToken);
            await NotifyJobActivityAsync(job.Id, initialActivity, DateTime.UtcNow);

            // Sync with origin before starting work (if this is a git repository)
            var workingDirectory = job.Project?.WorkingPath;
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                try
                {
                    var isGitRepo = await _versionControlService.IsGitRepositoryAsync(workingDirectory, cancellationToken);
                    if (isGitRepo)
                    {
                        _logger.LogInformation("Syncing with origin before job {JobId} execution", job.Id);

                        var syncActivity = "Syncing with remote repository...";
                        await UpdateHeartbeatAsync(job.Id, syncActivity, dbContext, cancellationToken);
                        await NotifyJobActivityAsync(job.Id, syncActivity, DateTime.UtcNow);

                        var syncResult = await _versionControlService.SyncWithOriginAsync(
                            workingDirectory,
                            remoteName: "origin",
                            progressCallback: progress =>
                            {
                                _logger.LogDebug("Git sync progress for job {JobId}: {Progress}", job.Id, progress);
                            },
                            cancellationToken: cancellationToken);

                        if (syncResult.Success)
                        {
                            _logger.LogInformation("Successfully synced with origin for job {JobId}. Branch: {Branch}, Commit: {Commit}",
                                job.Id, syncResult.BranchName, syncResult.CommitHash?[..Math.Min(8, syncResult.CommitHash?.Length ?? 0)]);
                        }
                        else
                        {
                            // Log warning but don't fail the job - the sync might fail if there's no remote
                            _logger.LogWarning("Failed to sync with origin for job {JobId}: {Error}. Continuing with local state.",
                                job.Id, syncResult.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during git sync for job {JobId}. Continuing with local state.", job.Id);
                }
            }

            // Capture git commit hash before execution for diff comparison later
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                try
                {
                    executionContext.GitCommitBefore = await _versionControlService.GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
                    if (!string.IsNullOrEmpty(executionContext.GitCommitBefore))
                    {
                        _logger.LogInformation("Captured git commit {Commit} before job {JobId} execution",
                            executionContext.GitCommitBefore[..Math.Min(8, executionContext.GitCommitBefore.Length)], job.Id);

                        // Store commit hash in database
                        var jobForGit = await dbContext.Jobs.FindAsync(new object[] { job.Id }, cancellationToken);
                        if (jobForGit != null)
                        {
                            jobForGit.GitCommitBefore = executionContext.GitCommitBefore;
                            await dbContext.SaveChangesAsync(cancellationToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture git commit hash for job {JobId}", job.Id);
                }
            }

            // Start a background task to monitor for cancellation requests and send heartbeats
            // Note: This task manages its own DbContext scopes to avoid disposal issues
            var cancellationMonitorTask = MonitorCancellationAndHeartbeatAsync(job.Id, executionContext, cancellationToken);

            // Execute the job with session support
            _logger.LogInformation("Starting provider execution for job {JobId} in directory {WorkingDir}",
                job.Id, workingDirectory ?? "(default)");

            // Multi-cycle execution support
            var effectiveMaxCycles = job.CycleMode == CycleMode.SingleCycle ? 1 : job.MaxCycles;
            var currentCycle = job.CurrentCycle;
            var sessionId = job.SessionId;
            ExecutionResult? lastResult = null;
            int? totalInputTokens = null;
            int? totalOutputTokens = null;
            decimal? totalCostUsd = null;

            // Track last progress update time to avoid excessive database writes
            var lastProgressUpdate = DateTime.MinValue;
            var progressUpdateInterval = TimeSpan.FromSeconds(2); // Update every 2 seconds to reduce database load
            var progressLock = new object();

            // Progress<T> doesn't properly handle async callbacks, so we use a synchronous handler
            // that fires updates in the background with proper scoping to avoid DbContext disposal issues
            var progress = new Progress<ExecutionProgress>(p =>
            {
                // Capture and store process ID and command as soon as they're reported
                if (p.ProcessId.HasValue && !executionContext.ProcessId.HasValue)
                {
                    executionContext.ProcessId = p.ProcessId.Value;
                    executionContext.CommandUsed = p.CommandUsed;
                    _logger.LogInformation("Captured process ID {ProcessId} for job {JobId}. Command: {Command}",
                        p.ProcessId.Value, job.Id, p.CommandUsed ?? "(unknown)");

                    // Notify UI about process start with full command
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_jobUpdateService != null)
                            {
                                await _jobUpdateService.NotifyProcessStarted(job.Id, p.ProcessId.Value,
                                    p.CommandUsed ?? $"{job.Provider?.Type} CLI");
                            }
                        }
                        catch { }
                    });

                    // Store process ID and command in database immediately
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var pidScope = _scopeFactory.CreateScope();
                            var pidDbContext = pidScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                            var jobForPid = await pidDbContext.Jobs.FindAsync(new object[] { job.Id });
                            if (jobForPid != null)
                            {
                                jobForPid.ProcessId = p.ProcessId.Value;
                                jobForPid.CommandUsed = p.CommandUsed;
                                await pidDbContext.SaveChangesAsync(CancellationToken.None);
                                _logger.LogDebug("Stored process ID {ProcessId} and command in database for job {JobId}",
                                    p.ProcessId.Value, job.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to store process ID/command for job {JobId}", job.Id);
                        }
                    });
                }

                // Stream output lines to UI in real-time AND accumulate in buffer for storage
                if (!string.IsNullOrEmpty(p.OutputLine))
                {
                    // Accumulate output for database storage
                    executionContext.AppendOutput(p.OutputLine, p.IsErrorOutput);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_jobUpdateService != null)
                            {
                                await _jobUpdateService.NotifyJobOutput(job.Id, p.OutputLine, p.IsErrorOutput, DateTime.UtcNow);
                            }
                        }
                        catch { }
                    });

                    // Detect if the CLI is requesting user interaction
                    if (!executionContext.IsPausedForInteraction)
                    {
                        var interactionRequest = InteractionDetector.DetectInteraction(
                            p.OutputLine,
                            executionContext.GetRecentOutputLines());

                        if (interactionRequest != null && interactionRequest.IsInteractionRequested && interactionRequest.Confidence >= 0.70)
                        {
                            _logger.LogInformation(
                                "Interaction detected for job {JobId}: Type={Type}, Confidence={Confidence:P0}, Prompt={Prompt}",
                                job.Id, interactionRequest.Type, interactionRequest.Confidence, interactionRequest.Prompt);

                            // Mark context as paused
                            executionContext.IsPausedForInteraction = true;
                            executionContext.CurrentInteractionRequest = interactionRequest;

                            // Update database and notify UI in background
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var interactionScope = _scopeFactory.CreateScope();
                                    var interactionJobService = interactionScope.ServiceProvider.GetRequiredService<IJobService>();
                                    var interactionDbContext = interactionScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

                                    // Serialize choices if available
                                    string? choicesJson = interactionRequest.Choices != null && interactionRequest.Choices.Count > 0
                                        ? JsonSerializer.Serialize(interactionRequest.Choices)
                                        : null;

                                    // Update job status in database
                                    await interactionJobService.PauseForInteractionAsync(
                                        job.Id,
                                        interactionRequest.Prompt ?? interactionRequest.RawOutput ?? "Interaction required",
                                        interactionRequest.Type.ToString(),
                                        choicesJson,
                                        CancellationToken.None);

                                    // Notify UI
                                    if (_jobUpdateService != null)
                                    {
                                        await _jobUpdateService.NotifyJobInteractionRequired(
                                            job.Id,
                                            interactionRequest.Prompt ?? interactionRequest.RawOutput ?? "Interaction required",
                                            interactionRequest.Type.ToString(),
                                            interactionRequest.Choices,
                                            interactionRequest.DefaultResponse);
                                    }

                                    await NotifyStatusChangedAsync(job.Id, JobStatus.Paused);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to pause job {JobId} for interaction", job.Id);
                                    executionContext.IsPausedForInteraction = false;
                                    executionContext.CurrentInteractionRequest = null;
                                }
                            });
                        }
                    }

                    return; // Don't process output lines as activity updates
                }

                var activity = !string.IsNullOrEmpty(p.ToolName)
                    ? $"Running tool: {p.ToolName}"
                    : (p.IsStreaming ? "Processing..." : p.CurrentMessage ?? "Working...");

                _logger.LogDebug("Job {JobId} progress: {Activity}", job.Id, activity);

                // Throttle progress updates to avoid database overload
                var now = DateTime.UtcNow;
                bool shouldUpdate;
                lock (progressLock)
                {
                    shouldUpdate = now - lastProgressUpdate >= progressUpdateInterval;
                    if (shouldUpdate)
                    {
                        lastProgressUpdate = now;
                    }
                }

                if (shouldUpdate)
                {
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
                else
                {
                    // Still send SignalR notification for real-time UI updates, just skip database
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await NotifyJobActivityAsync(job.Id, activity, now);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to send activity notification for job {JobId}", job.Id);
                        }
                    });
                }
            });

            // ===== Multi-Cycle Execution Loop =====
            var currentPrompt = job.GoalPrompt;
            var cycleComplete = false;

            while (currentCycle <= effectiveMaxCycles && !cycleComplete && !cancellationToken.IsCancellationRequested)
            {
                if (effectiveMaxCycles > 1)
                {
                    _logger.LogInformation("Starting cycle {Current}/{Max} for job {JobId}",
                        currentCycle, effectiveMaxCycles, job.Id);

                    // Notify UI about cycle progress
                    if (_jobUpdateService != null)
                    {
                        await _jobUpdateService.NotifyJobCycleProgress(job.Id, currentCycle, effectiveMaxCycles);
                    }

                    // Update current cycle in database
                    using var cycleScope = _scopeFactory.CreateScope();
                    var cycleDbContext = cycleScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                    var jobForCycle = await cycleDbContext.Jobs.FindAsync(new object[] { job.Id }, cancellationToken);
                    if (jobForCycle != null)
                    {
                        jobForCycle.CurrentCycle = currentCycle;
                        await cycleDbContext.SaveChangesAsync(cancellationToken);
                    }
                }

                // Determine session ID for this cycle
                var cycleSessionId = job.CycleSessionMode == CycleSessionMode.ContinueSession ? sessionId : null;

                var result = await provider.ExecuteWithOptionsAsync(
                    currentPrompt,
                    new ExecutionOptions
                    {
                        SessionId = cycleSessionId,
                        WorkingDirectory = workingDirectory,
                        McpConfigPath = await GetMcpConfigPathAsync(job.ProviderId),
                        Model = job.ModelUsed,
                        Title = job.Title
                    },
                    progress,
                    cancellationToken);

                // Store last result and accumulate tokens/cost
                lastResult = result;
                sessionId = result.SessionId ?? sessionId;
                if (result.InputTokens.HasValue)
                    totalInputTokens = (totalInputTokens ?? 0) + result.InputTokens.Value;
                if (result.OutputTokens.HasValue)
                    totalOutputTokens = (totalOutputTokens ?? 0) + result.OutputTokens.Value;
                if (result.CostUsd.HasValue)
                    totalCostUsd = (totalCostUsd ?? 0) + result.CostUsd.Value;

                // Check for cycle completion conditions
                if (!result.Success)
                {
                    _logger.LogWarning("Cycle {Current} failed for job {JobId}: {Error}",
                        currentCycle, job.Id, result.ErrorMessage);
                    cycleComplete = true;
                    break;
                }

                // Check cancellation between cycles
                using var cancelCheckScope = _scopeFactory.CreateScope();
                var cancelCheckService = cancelCheckScope.ServiceProvider.GetRequiredService<IJobService>();
                if (await cancelCheckService.IsCancellationRequestedAsync(job.Id, CancellationToken.None))
                {
                    _logger.LogInformation("Job {JobId} cancelled between cycles", job.Id);
                    cycleComplete = true;
                    break;
                }

                // Determine if we should continue cycling
                if (job.CycleMode == CycleMode.SingleCycle || currentCycle >= effectiveMaxCycles)
                {
                    cycleComplete = true;
                }
                else if (job.CycleMode == CycleMode.Autonomous)
                {
                    // Check if output contains CYCLE_COMPLETE marker
                    if (result.Output?.Contains("CYCLE_COMPLETE", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _logger.LogInformation("Job {JobId} completed autonomously at cycle {Current}",
                            job.Id, currentCycle);
                        cycleComplete = true;
                    }
                    else
                    {
                        // Build next cycle prompt for autonomous mode
                        currentPrompt = string.IsNullOrWhiteSpace(job.CycleReviewPrompt)
                            ? "Review all changes made so far. Verify the code compiles and tests pass. If the task is complete and working correctly, respond with CYCLE_COMPLETE. Otherwise, continue implementing the remaining work."
                            : job.CycleReviewPrompt;
                        currentCycle++;
                    }
                }
                else if (job.CycleMode == CycleMode.FixedCount)
                {
                    // Build next cycle prompt for fixed count mode
                    currentPrompt = $"Continue implementing the task. This is cycle {currentCycle + 1} of {effectiveMaxCycles}. Review the current state and continue where you left off.";
                    currentCycle++;
                }
            }

            // Use accumulated results
            var finalResult = lastResult ?? new ExecutionResult { Success = false, ErrorMessage = "No execution result" };
            finalResult.InputTokens = totalInputTokens;
            finalResult.OutputTokens = totalOutputTokens;
            finalResult.CostUsd = totalCostUsd;

            // Stop monitoring cancellation
            executionContext.CancellationTokenSource?.Cancel();
            try { await cancellationMonitorTask; } catch { }

            // Re-fetch job state from database to check cancellation
            using var checkScope = _scopeFactory.CreateScope();
            var checkJobService = checkScope.ServiceProvider.GetRequiredService<IJobService>();
            var wasCancelled = await checkJobService.IsCancellationRequestedAsync(job.Id, CancellationToken.None);

            if (wasCancelled)
            {
                await CompleteJobAsync(job.Id, JobStatus.Cancelled, finalResult.SessionId, finalResult.Output,
                    "Job was cancelled by user", finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                    executionContext, workingDirectory, dbContext, CancellationToken.None);

                // Record usage even for cancelled jobs
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, job.Provider.Name, job.Id, finalResult, CancellationToken.None);

                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled by user");

                _logger.LogInformation("Job {JobId} was cancelled during execution", job.Id);
            }
            else if (finalResult.Success)
            {
                // Save messages
                if (finalResult.Messages.Count > 0)
                {
                    var messages = finalResult.Messages.Select(m => new JobMessage
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

                await CompleteJobAsync(job.Id, JobStatus.Completed, finalResult.SessionId, finalResult.Output,
                    null, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                    executionContext, workingDirectory, dbContext, CancellationToken.None);

                // Record usage after successful completion
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, job.Provider.Name, job.Id, finalResult, CancellationToken.None);

                _logger.LogInformation("Job {JobId} completed successfully. Session: {SessionId}, InputTokens: {InputTokens}, OutputTokens: {OutputTokens}, Cost: {CostUsd}",
                    job.Id, finalResult.SessionId, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd);
                await NotifyJobCompletedAsync(job.Id, true);
            }
            else
            {
                await CompleteJobAsync(job.Id, JobStatus.Failed, finalResult.SessionId, finalResult.Output,
                    finalResult.ErrorMessage, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                    executionContext, workingDirectory, dbContext, CancellationToken.None);

                // Record usage even for failed jobs
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, job.Provider.Name, job.Id, finalResult, CancellationToken.None);

                _logger.LogWarning("Job {JobId} failed: {Error}. InputTokens: {InputTokens}, OutputTokens: {OutputTokens}, Cost: {CostUsd}",
                    job.Id, finalResult.ErrorMessage, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd);
                await NotifyJobCompletedAsync(job.Id, false, finalResult.ErrorMessage);
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
        finally
        {
            // Dispose SDK providers that implement IAsyncDisposable
            if (executionContext.ProviderInstance is IAsyncDisposable disposable)
            {
                try
                {
                    await disposable.DisposeAsync();
                }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx, "Error disposing provider for job {JobId}", job.Id);
                }
            }
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
    /// Records usage from an execution result and checks for exhaustion warnings.
    /// </summary>
    private async Task RecordUsageAndCheckExhaustionAsync(
        Guid providerId,
        string providerName,
        Guid jobId,
        ExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (_providerUsageService == null)
            return;

        try
        {
            // Record the usage
            await _providerUsageService.RecordUsageAsync(providerId, jobId, result, cancellationToken);

            // Check for exhaustion warning and broadcast via SignalR
            var warning = await _providerUsageService.CheckExhaustionAsync(providerId, cancellationToken: cancellationToken);
            if (warning != null && _jobUpdateService != null)
            {
                await _jobUpdateService.NotifyProviderUsageWarning(
                    providerId,
                    providerName,
                    warning.PercentUsed,
                    warning.Message,
                    warning.IsExhausted,
                    warning.ResetTime);

                if (warning.IsExhausted)
                {
                    _logger.LogWarning("Provider {ProviderName} has reached usage limit after job {JobId}", providerName, jobId);
                }
                else
                {
                    _logger.LogInformation("Provider {ProviderName} is at {PercentUsed}% usage after job {JobId}",
                        providerName, warning.PercentUsed, jobId);
                }
            }
        }
        catch (Exception ex)
        {
            // Don't fail job processing due to usage tracking errors
            _logger.LogWarning(ex, "Failed to record usage for job {JobId}", jobId);
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
    /// Completes a job with full result data, console output, and git diff
    /// </summary>
    private async Task CompleteJobAsync(
        Guid jobId, JobStatus status, string? sessionId, string? output, string? errorMessage,
        int? inputTokens, int? outputTokens, decimal? costUsd, string? modelUsed,
        JobExecutionContext executionContext, string? workingDirectory,
        VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs
            .Include(j => j.Project)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
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
            job.ModelUsed = modelUsed ?? job.ModelUsed;
            job.WorkerInstanceId = null;
            job.LastHeartbeatAt = null;
            job.ProcessId = null;
            job.CurrentActivity = null;

            // Store accumulated console output
            var consoleOutput = executionContext.GetConsoleOutput();
            if (!string.IsNullOrEmpty(consoleOutput))
            {
                job.ConsoleOutput = consoleOutput;
            }

            // Generate and store git diff if applicable
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                try
                {
                    // Get diff since the commit we captured at start
                    var baseCommit = executionContext.GitCommitBefore;
                    string? gitDiff = null;
                    IReadOnlyList<string>? commitLog = null;

                    if (!string.IsNullOrEmpty(baseCommit))
                    {
                        // First, get committed changes since the base commit (commits made by the agent)
                        var committedDiff = await _versionControlService.GetCommitRangeDiffAsync(workingDirectory, baseCommit, null, cancellationToken);

                        // Get commit log (commit messages made by the agent)
                        commitLog = await _versionControlService.GetCommitLogAsync(workingDirectory, baseCommit, null, cancellationToken);
                        if (commitLog.Count > 0)
                        {
                            _logger.LogInformation("Found {Count} commits made during job {JobId} execution", commitLog.Count, jobId);
                        }

                        // Also get any uncommitted changes (working directory changes)
                        var uncommittedDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, null, cancellationToken);

                        // Combine both diffs
                        if (!string.IsNullOrEmpty(committedDiff) && !string.IsNullOrEmpty(uncommittedDiff))
                        {
                            gitDiff = $"=== Committed changes since {baseCommit} ===\n{committedDiff}\n\n=== Uncommitted changes ===\n{uncommittedDiff}";
                        }
                        else if (!string.IsNullOrEmpty(committedDiff))
                        {
                            gitDiff = committedDiff;
                        }
                        else if (!string.IsNullOrEmpty(uncommittedDiff))
                        {
                            gitDiff = uncommittedDiff;
                        }
                    }
                    else
                    {
                        // No base commit captured, just get uncommitted changes
                        gitDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, null, cancellationToken);
                    }

                    if (!string.IsNullOrEmpty(gitDiff))
                    {
                        job.GitDiff = gitDiff;
                        _logger.LogInformation("Captured git diff for job {JobId}: {Length} chars", jobId, gitDiff.Length);

                        // Generate session summary from git diff for pre-populating commit messages
                        // Pass the commit log so we can include agent commit messages as bullet points
                        var sessionSummary = JobSummaryGenerator.GenerateSummary(job, commitLog);
                        if (!string.IsNullOrWhiteSpace(sessionSummary))
                        {
                            job.SessionSummary = sessionSummary;
                            _logger.LogInformation("Generated session summary for job {JobId}: {Summary}",
                                jobId, sessionSummary.Length > 100 ? sessionSummary[..100] + "..." : sessionSummary);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No git changes detected for job {JobId}", jobId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture git diff for job {JobId}", jobId);
                }

                // Perform auto-commit if configured and job completed successfully
                if (status == JobStatus.Completed && job.Project?.AutoCommitMode != AutoCommitMode.Off)
                {
                    await PerformAutoCommitAsync(job, workingDirectory, cancellationToken);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Performs auto-commit (and optionally push) based on project settings.
    /// </summary>
    private async Task PerformAutoCommitAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            // Check if there are uncommitted changes
            var hasChanges = await _versionControlService.HasUncommittedChangesAsync(workingDirectory, cancellationToken);
            if (!hasChanges)
            {
                _logger.LogDebug("No uncommitted changes to auto-commit for job {JobId}", job.Id);
                return;
            }

            var commitMessage = job.SessionSummary ?? $"VibeSwarm: {job.Title ?? "Job completed"}";

            _logger.LogInformation("Auto-committing changes for job {JobId} with mode {Mode}",
                job.Id, job.Project!.AutoCommitMode);

            var commitResult = await _versionControlService.CommitAllChangesAsync(
                workingDirectory,
                commitMessage,
                cancellationToken);

            if (commitResult.Success)
            {
                job.GitCommitHash = commitResult.CommitHash;
                _logger.LogInformation("Auto-committed changes for job {JobId}: {CommitHash}",
                    job.Id, commitResult.CommitHash?[..Math.Min(8, commitResult.CommitHash?.Length ?? 0)]);

                // Push if configured
                if (job.Project!.AutoCommitMode == AutoCommitMode.CommitAndPush)
                {
                    var pushResult = await _versionControlService.PushAsync(workingDirectory, cancellationToken: cancellationToken);
                    if (pushResult.Success)
                    {
                        _logger.LogInformation("Auto-pushed changes for job {JobId}", job.Id);
                    }
                    else
                    {
                        // Push failed, but commit succeeded - log warning but don't fail the job
                        _logger.LogWarning("Auto-push failed for job {JobId}: {Error}. Changes were committed but not pushed.",
                            job.Id, pushResult.Error);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Auto-commit failed for job {JobId}: {Error}", job.Id, commitResult.Error);
            }
        }
        catch (Exception ex)
        {
            // Auto-commit failures should not fail the job
            _logger.LogWarning(ex, "Error during auto-commit for job {JobId}", job.Id);
        }
    }

    /// <summary>
    /// Monitors for cancellation requests and sends regular heartbeats.
    /// Does NOT use the passed dbContext - creates its own scopes to avoid disposal issues.
    /// </summary>
    private async Task MonitorCancellationAndHeartbeatAsync(
        Guid jobId,
        JobExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var heartbeatInterval = TimeSpan.FromSeconds(30); // Reduced frequency to avoid database contention
        var cancellationCheckInterval = TimeSpan.FromSeconds(5); // Check cancellation less frequently
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Check for cancellation request with a fresh scope
                    using (var checkScope = _scopeFactory.CreateScope())
                    {
                        var checkDbContext = checkScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                        var job = await checkDbContext.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

                        if (job?.CancellationRequested == true)
                        {
                            _logger.LogInformation("Cancellation requested for job {JobId}", jobId);
                            executionContext.CancellationTokenSource?.Cancel();
                            break;
                        }
                    }

                    // Send heartbeat periodically with a fresh scope
                    var now = DateTime.UtcNow;
                    if (now - lastHeartbeat >= heartbeatInterval)
                    {
                        lastHeartbeat = now;
                        using (var heartbeatScope = _scopeFactory.CreateScope())
                        {
                            var heartbeatDbContext = heartbeatScope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
                            var heartbeatJob = await heartbeatDbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
                            if (heartbeatJob != null)
                            {
                                heartbeatJob.LastHeartbeatAt = now;

                                // Persist console output buffer periodically so page refreshes show accumulated output
                                var currentOutput = executionContext.GetConsoleOutput();
                                if (!string.IsNullOrEmpty(currentOutput))
                                {
                                    heartbeatJob.ConsoleOutput = currentOutput;
                                }

                                await heartbeatDbContext.SaveChangesAsync(cancellationToken);
                            }
                        }
                        _logger.LogDebug("Sent heartbeat for job {JobId}", jobId);

                        // Send SignalR heartbeat notification
                        if (_jobUpdateService != null)
                        {
                            try
                            {
                                await _jobUpdateService.NotifyJobHeartbeat(jobId, now);
                            }
                            catch { }
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Scope was disposed, create a new one on next iteration
                    _logger.LogWarning("DbContext was disposed in heartbeat monitor for job {JobId}, will retry", jobId);
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
        return (config.Type, config.ConnectionMode) switch
        {
            (ProviderType.Claude, ProviderConnectionMode.SDK) => new ClaudeSdkProvider(config),
            (ProviderType.Copilot, ProviderConnectionMode.SDK) => new CopilotSdkProvider(config),
            (ProviderType.OpenCode, _) => new OpenCodeProvider(config),
            (ProviderType.Claude, _) => new ClaudeProvider(config),
            (ProviderType.Copilot, _) => new CopilotProvider(config),
            _ => throw new NotSupportedException($"Provider type {config.Type} with mode {config.ConnectionMode} is not supported.")
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

    /// <summary>
    /// Gets the MCP config file path for the given provider.
    /// Generates a temporary MCP config file containing all enabled skills.
    /// </summary>
    private async Task<string?> GetMcpConfigPathAsync(Guid providerId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mcpConfigService = scope.ServiceProvider.GetRequiredService<IMcpConfigService>();
            var providerService = scope.ServiceProvider.GetRequiredService<IProviderService>();

            // Get the provider to determine its type
            var provider = await providerService.GetByIdAsync(providerId);
            if (provider == null)
            {
                _logger.LogWarning("Could not find provider {ProviderId} to generate MCP config", providerId);
                return null;
            }

            // Generate the MCP config file
            var configPath = await mcpConfigService.GenerateMcpConfigFileAsync();
            if (!string.IsNullOrEmpty(configPath))
            {
                _logger.LogDebug("Generated MCP config at {ConfigPath} for provider {ProviderId}", configPath, providerId);
            }

            return configPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate MCP config for provider {ProviderId}", providerId);
            return null;
        }
    }
}
