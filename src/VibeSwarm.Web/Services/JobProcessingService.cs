using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared;

namespace VibeSwarm.Web.Services;

public class JobProcessingService : BackgroundService
{
    private sealed class GitCheckpointRequiredException : InvalidOperationException
    {
        public GitCheckpointRequiredException(string message)
            : base(message)
        {
        }
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobProcessingService> _logger;
    private readonly IJobUpdateService? _jobUpdateService;
    private readonly IJobCoordinatorService? _jobCoordinator;
    private readonly IProviderHealthTracker? _healthTracker;
    private readonly ProcessSupervisor? _processSupervisor;
    private readonly IVersionControlService _versionControlService;
    private readonly IInteractionResponseService? _interactionResponseService;
    private readonly IProjectEnvironmentCredentialService _projectEnvironmentCredentialService;
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
        ProcessSupervisor? processSupervisor = null,
        IInteractionResponseService? interactionResponseService = null,
        IProjectEnvironmentCredentialService? projectEnvironmentCredentialService = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _versionControlService = versionControlService;
        _jobUpdateService = jobUpdateService;
        _jobCoordinator = jobCoordinator;
        _healthTracker = healthTracker;
        _processSupervisor = processSupervisor;
        _interactionResponseService = interactionResponseService;
        _projectEnvironmentCredentialService = projectEnvironmentCredentialService ?? throw new ArgumentNullException(nameof(projectEnvironmentCredentialService));
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

            // Fix jobs that completed (CompletedAt set) but crashed before persisting terminal status
            var completedWrongStatus = await dbContext.Jobs
                .Where(j => j.Status == JobStatus.Started || j.Status == JobStatus.Processing)
                .Where(j => j.CompletedAt.HasValue)
                .ToListAsync(cancellationToken);

            foreach (var job in completedWrongStatus)
            {
                _logger.LogWarning("Fixing job {JobId} with completed timestamp but non-terminal status {Status}",
                    job.Id, job.Status);
                job.Status = JobStatus.Completed;
                job.WorkerInstanceId = null;
                job.ProcessId = null;
                job.CurrentActivity = null;
            }

            if (completedWrongStatus.Any())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Fixed {Count} jobs with completed timestamp but non-terminal status", completedWrongStatus.Count);
            }

            // Find jobs that were being processed by any worker but appear orphaned
            // (Started/Processing with old heartbeats, excluding already-completed jobs)
            var cutoffTime = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            var orphanedJobs = await dbContext.Jobs
                .Include(j => j.Project)
                .Where(j => (j.Status == JobStatus.Started || j.Status == JobStatus.Processing))
                .Where(j => !j.CompletedAt.HasValue)
                .Where(j => !j.LastHeartbeatAt.HasValue || j.LastHeartbeatAt.Value < cutoffTime)
                .ToListAsync(cancellationToken);

            foreach (var job in orphanedJobs)
            {
                _logger.LogWarning("Found orphaned job {JobId} from worker {WorkerId}, resetting for retry",
                    job.Id, job.WorkerInstanceId ?? "unknown");

                if (await TryPreserveChangesForRecoveryAsync(
                    job,
                    "Worker crashed or became unresponsive before job changes were finalized.",
                    cancellationToken))
                {
                    continue;
                }

                if (job.MaxRetries == 0 || job.RetryCount < job.MaxRetries)
                {
                    JobStateMachine.TryTransition(job, JobStatus.New, "Automatic orphan recovery");
                    job.RetryCount++;
                    job.ErrorMessage = "Worker crashed or became unresponsive. Automatic recovery.";
                }
                else
                {
                    JobStateMachine.TryTransition(job, JobStatus.Failed, "Automatic orphan recovery exhausted retries");
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
            _logger.LogWarning("Failed to preserve workspace changes for recovered job {JobId}: {Error}", job.Id, preserveResult.Error);
            return false;
        }

        var transition = JobStateMachine.TryTransition(job, JobStatus.Stalled, reason);
        if (!transition.Success)
        {
            _logger.LogWarning("Failed to move recovered job {JobId} into stalled state: {Error}", job.Id, transition.ErrorMessage);
            return false;
        }

        job.GitDiff = !string.IsNullOrWhiteSpace(diff) ? diff : job.GitDiff;
        job.ChangedFilesCount = workingTreeStatus.ChangedFilesCount;
        job.WorkerInstanceId = null;
        job.LastHeartbeatAt = null;
        job.ProcessId = null;
        job.CurrentActivity = null;
        job.ErrorMessage = $"{reason} Preserved {workingTreeStatus.ChangedFilesCount} changed file(s) in {preserveResult.SavedReference ?? "stash@{0}"} for recovery.";

        return true;
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

        string? workingDirectory = null;
        string? projectMemoryFilePath = null;
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

            // Claim ownership before any provider work starts so duplicate schedulers cannot
            // launch the same CLI agent twice for a single job.
            var claimed = await ClaimJobAsync(job.Id, dbContext, cancellationToken);
            if (!claimed)
            {
                _logger.LogInformation("Skipping provider execution for job {JobId} because another worker already claimed it.", job.Id);
                return;
            }

            job.Status = JobStatus.Started;
            job.StartedAt ??= DateTime.UtcNow;
            job.LastActivityAt = DateTime.UtcNow;
            job.WorkerInstanceId = _workerInstanceId;
            job.LastHeartbeatAt = DateTime.UtcNow;
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
            using var usageScope = _scopeFactory.CreateScope();
            var providerUsageService = usageScope.ServiceProvider.GetService<IProviderUsageService>();
            if (providerUsageService != null)
            {
                var exhaustionWarning = await providerUsageService.CheckExhaustionAsync(job.ProviderId, cancellationToken: cancellationToken);
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

            // Prepare the configured working branch before starting work (if this is a git repository)
            workingDirectory = job.Project?.WorkingPath;
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                try
                {
                    var isGitRepo = await _versionControlService.IsGitRepositoryAsync(workingDirectory, cancellationToken);
                    if (isGitRepo)
                    {
                        var checkpointBaseBranch = await PreserveWorkingTreeBeforeBranchPreparationAsync(
                            job,
                            workingDirectory,
                            dbContext,
                            captureJobDiff: false,
                            reason: "Protected local changes before preparing the job branch.",
                            cancellationToken: cancellationToken);

                        var branchActivity = string.IsNullOrWhiteSpace(job.Branch)
                            ? "Syncing working branch..."
                            : $"Preparing branch '{job.Branch}'...";
                        await UpdateHeartbeatAsync(job.Id, branchActivity, dbContext, cancellationToken);
                        await NotifyJobActivityAsync(job.Id, branchActivity, DateTime.UtcNow);

                        await PrepareWorkingBranchAsync(job, workingDirectory, checkpointBaseBranch, cancellationToken);
                    }
                }
                catch (GitCheckpointRequiredException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error preparing git branch for job {JobId}. Continuing with local state.", job.Id);
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

            // Auto-generate or refresh repo map if stale (>24 hours) or missing
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory) && job.Project != null)
            {
                try
                {
                    if (job.Project.RepoMap == null || job.Project.RepoMapGeneratedAt == null ||
                        job.Project.RepoMapGeneratedAt < DateTime.UtcNow.AddHours(-24))
                    {
                        _logger.LogInformation("Generating repo map for project {ProjectName} (job {JobId})", job.Project.Name, job.Id);
                        var repoMap = RepoMapGenerator.GenerateRepoMap(workingDirectory);
                        if (repoMap != null)
                        {
                            var projectForMap = await dbContext.Projects.FindAsync(new object[] { job.Project.Id }, cancellationToken);
                            if (projectForMap != null)
                            {
                                projectForMap.RepoMap = repoMap;
                                projectForMap.RepoMapGeneratedAt = DateTime.UtcNow;
                                await dbContext.SaveChangesAsync(cancellationToken);
                                // Update the in-memory project reference
                                job.Project.RepoMap = repoMap;
                                job.Project.RepoMapGeneratedAt = projectForMap.RepoMapGeneratedAt;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate repo map for job {JobId}", job.Id);
                }
            }

            // Load app settings for prompt structuring and efficiency rules
            var appSettings = await dbContext.AppSettings.FirstOrDefaultAsync(cancellationToken);

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

                // Log provider connection state changes
                if (!string.IsNullOrEmpty(p.OutputLine) && p.OutputLine.StartsWith("[Connection]"))
                {
                    _logger.LogInformation("Job {JobId} provider connection state: {State}", job.Id, p.OutputLine);
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
            if (job.Project != null)
            {
                _projectEnvironmentCredentialService.PopulateForExecution(job.Project);
            }

            var enableStructuring = appSettings?.EnablePromptStructuring ?? true;
            var currentPrompt = PromptBuilder.BuildStructuredPrompt(job, enableStructuring);
            var cycleComplete = false;

            // Build system prompt rules for agent efficiency
            var injectEfficiencyRules = appSettings?.InjectEfficiencyRules ?? true;
            var injectRepoMap = appSettings?.InjectRepoMap ?? true;
            var systemPromptRules = PromptBuilder.BuildSystemPromptRules(job.Project, injectEfficiencyRules, injectRepoMap);
            projectMemoryFilePath = await PrepareProjectMemoryFileAsync(job.Project, cancellationToken);
            var projectMemoryRules = PromptBuilder.BuildProjectMemoryRules(job.Project, projectMemoryFilePath);
            if (!string.IsNullOrWhiteSpace(projectMemoryRules))
            {
                systemPromptRules = string.IsNullOrWhiteSpace(systemPromptRules)
                    ? projectMemoryRules
                    : $"{systemPromptRules}{Environment.NewLine}{Environment.NewLine}{projectMemoryRules}";
            }

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

                if (currentCycle == job.CurrentCycle)
                {
                    await RecordProviderAttemptAsync(
                        job.Id,
                        job.ProviderId,
                        job.Provider?.Name ?? provider.Name,
                        job.ModelUsed,
                        job.ActiveExecutionIndex,
                        "initial-execution",
                        dbContext,
                        cancellationToken);
                }

                var mcpOptions = await GetMcpExecutionOptionsAsync(job.ProviderId, job.Project, workingDirectory, cancellationToken);

                var result = await provider.ExecuteWithOptionsAsync(
                    currentPrompt,
                    new ExecutionOptions
                    {
                        SessionId = cycleSessionId,
                        WorkingDirectory = workingDirectory,
                        McpConfigPath = mcpOptions.McpConfigPath,
                        AdditionalArgs = mcpOptions.AdditionalArgs,
                        Model = job.ModelUsed,
                        Title = job.Title,
                        AppendSystemPrompt = systemPromptRules
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
                await UpdateProviderAttemptOutcomeAsync(job.Id, job.ActiveExecutionIndex, false, finalResult.ModelUsed ?? job.ModelUsed, dbContext, CancellationToken.None);
                var providerDisplayName = job.Provider?.Name;
                providerDisplayName ??= provider?.Name;
                providerDisplayName ??= "Unknown Provider";
                await CompleteJobAsync(job.Id, JobStatus.Cancelled, finalResult.SessionId, finalResult.Output,
                    "Job was cancelled by user", finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                    executionContext, workingDirectory, dbContext, CancellationToken.None);

                // Record usage even for cancelled jobs
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, providerDisplayName, job.Id, finalResult, provider!, CancellationToken.None);

                await NotifyJobCompletedAsync(job.Id, false, "Job was cancelled by user");

                _logger.LogInformation("Job {JobId} was cancelled during execution", job.Id);
            }
            else if (finalResult.Success)
            {
                await UpdateProviderAttemptOutcomeAsync(job.Id, job.ActiveExecutionIndex, true, finalResult.ModelUsed ?? job.ModelUsed, dbContext, CancellationToken.None);
                var providerDisplayName = job.Provider?.Name;
                providerDisplayName ??= provider?.Name;
                providerDisplayName ??= "Unknown Provider";
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

                var hasGitChanges = await CompleteJobAsync(job.Id, JobStatus.Completed, finalResult.SessionId, finalResult.Output,
                    null, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                    executionContext, workingDirectory, dbContext, CancellationToken.None);

                if (hasGitChanges)
                {
                    await NotifyJobGitDiffUpdatedAsync(job.Id, true);
                }

                // Record usage after successful completion
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, providerDisplayName, job.Id, finalResult, provider!, CancellationToken.None);

                _logger.LogInformation("Job {JobId} completed successfully. Session: {SessionId}, InputTokens: {InputTokens}, OutputTokens: {OutputTokens}, Cost: {CostUsd}",
                    job.Id, finalResult.SessionId, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd);
                await NotifyJobCompletedAsync(job.Id, true);
            }
            else
            {
                await UpdateProviderAttemptOutcomeAsync(job.Id, job.ActiveExecutionIndex, false, finalResult.ModelUsed ?? job.ModelUsed, dbContext, CancellationToken.None);
                var providerDisplayName = job.Provider?.Name;
                providerDisplayName ??= provider?.Name;
                providerDisplayName ??= "Unknown Provider";
                await CompleteJobAsync(job.Id, JobStatus.Failed, finalResult.SessionId, finalResult.Output,
                    finalResult.ErrorMessage, finalResult.InputTokens, finalResult.OutputTokens, finalResult.CostUsd, finalResult.ModelUsed,
                    executionContext, workingDirectory, dbContext, CancellationToken.None);

                // Record usage even for failed jobs
                await RecordUsageAndCheckExhaustionAsync(job.ProviderId, providerDisplayName, job.Id, finalResult, provider!, CancellationToken.None);

                // System-level errors (model unavailable, upstream outages) should immediately
                // trip the circuit breaker to prevent cascading failures on queued jobs
                if (finalResult.IsSystemError && _healthTracker != null)
                {
                    _healthTracker.RecordSystemFailure(job.ProviderId, finalResult.ErrorMessage);
                    _logger.LogWarning("Job {JobId} failed with system error, circuit breaker tripped for provider {ProviderId}: {Error}",
                        job.Id, job.ProviderId, finalResult.ErrorMessage);
                }

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
                    if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                    {
                        try
                        {
                            await PreserveWorkingTreeBeforeBranchPreparationAsync(
                                jobEntity,
                                workingDirectory,
                                resetDbContext,
                                captureJobDiff: true,
                                reason: jobEntity.CancellationRequested
                                    ? "Preserved local changes after the job was cancelled."
                                    : "Preserved local changes after the worker shut down during execution.",
                                cancellationToken: CancellationToken.None);
                        }
                        catch (Exception checkpointEx)
                        {
                            _logger.LogWarning(checkpointEx, "Failed to preserve local changes for cancelled job {JobId}", job.Id);
                        }
                    }

                    if (jobEntity.CancellationRequested)
                    {
                        // User requested cancellation
                        JobStateMachine.TryTransition(jobEntity, JobStatus.Cancelled, "Job was cancelled by user.");
                        jobEntity.ErrorMessage = "Job was cancelled by user";
                    }
                    else
                    {
                        // Service shutdown or timeout - reset for retry
                        JobStateMachine.TryTransition(jobEntity, JobStatus.New, "Service shutdown during execution. Queued for retry.");
                        jobEntity.ErrorMessage = jobEntity.GitCheckpointStatus == GitCheckpointStatus.Preserved
                            ? "Service shutdown during execution. Queued for retry after preserving local changes."
                            : "Service shutdown during execution. Queued for retry.";
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
            if (!string.IsNullOrWhiteSpace(projectMemoryFilePath))
            {
                try
                {
                    await PersistProjectMemoryAsync(job.Project?.Id, projectMemoryFilePath, CancellationToken.None);
                }
                catch (Exception memoryEx)
                {
                    _logger.LogWarning(memoryEx, "Failed to persist project memory for job {JobId}", job.Id);
                }
            }

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
	private async Task<bool> ClaimJobAsync(Guid jobId, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var claimTime = DateTime.UtcNow;
        var updatedRows = await dbContext.Jobs
            .Where(j => j.Id == jobId)
            .Where(j => (j.Status == JobStatus.New || j.Status == JobStatus.Pending) && !j.CancellationRequested)
            .Where(j => j.WorkerInstanceId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, JobStatus.Started)
                .SetProperty(j => j.WorkerInstanceId, _workerInstanceId)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? claimTime)
                .SetProperty(j => j.LastActivityAt, claimTime)
                .SetProperty(j => j.LastHeartbeatAt, claimTime),
                cancellationToken);

        if (updatedRows == 1)
        {
            return true;
        }

        var existingJob = await dbContext.Jobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new { j.Status, j.WorkerInstanceId, j.CancellationRequested })
            .FirstOrDefaultAsync(cancellationToken);

        if (existingJob == null)
        {
            _logger.LogWarning("Failed to claim job {JobId}: job no longer exists.", jobId);
            return false;
        }

        _logger.LogWarning(
            "Failed to claim job {JobId}: status {Status}, worker {WorkerId}, cancellation requested {CancellationRequested}.",
            jobId,
            existingJob.Status,
            existingJob.WorkerInstanceId ?? "(none)",
            existingJob.CancellationRequested);

        return false;
    }

    /// <summary>
    /// Records usage from an execution result and checks for exhaustion warnings.
    /// </summary>
    private async Task RecordUsageAndCheckExhaustionAsync(
        Guid providerId,
        string providerName,
        Guid jobId,
        ExecutionResult result,
        IProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            using var usageScope = _scopeFactory.CreateScope();
            var providerUsageService = usageScope.ServiceProvider.GetService<IProviderUsageService>();
            if (providerUsageService == null)
                return;

            var usageResult = await RefreshProviderUsageAsync(provider, result, cancellationToken);

            // Record the usage
            await providerUsageService.RecordUsageAsync(providerId, jobId, usageResult, cancellationToken);

            // Check for exhaustion warning and broadcast via SignalR
            var warning = await providerUsageService.CheckExhaustionAsync(providerId, cancellationToken: cancellationToken);
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

    private async Task<ExecutionResult> RefreshProviderUsageAsync(
        IProvider provider,
        ExecutionResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var latestLimits = await provider.GetUsageLimitsAsync(cancellationToken);
            if (ShouldApplyProviderUsage(latestLimits))
            {
                result.DetectedUsageLimits = MergeUsageLimits(result.DetectedUsageLimits, latestLimits);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to refresh provider usage snapshot for provider {ProviderId}", provider.Id);
        }

        return result;
    }

    private static bool ShouldApplyProviderUsage(UsageLimits? limits)
    {
        return limits != null && (
            limits.IsLimitReached ||
            limits.CurrentUsage.HasValue ||
            limits.MaxUsage.HasValue ||
            limits.ResetTime.HasValue ||
            limits.Windows.Count > 0);
    }

    private static UsageLimits MergeUsageLimits(UsageLimits? existing, UsageLimits latest)
    {
        return UsageLimitWindowHelper.Merge(existing, latest);
    }

    /// <summary>
    /// Releases ownership of a job and sets final status
    /// </summary>
    private async Task ReleaseJobAsync(Guid jobId, JobStatus status, string? errorMessage, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            JobStateMachine.TryTransition(job, status, errorMessage);
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
            var transitioned = JobStateMachine.TryTransition(job, status, $"Internal transition to {status}.");
            if (!transitioned.Success)
            {
                _logger.LogWarning("Failed to update job {JobId} to {Status}: {Error}", jobId, status, transitioned.ErrorMessage);
                return;
            }

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
    private async Task<bool> CompleteJobAsync(
        Guid jobId, JobStatus status, string? sessionId, string? output, string? errorMessage,
        int? inputTokens, int? outputTokens, decimal? costUsd, string? modelUsed,
        JobExecutionContext executionContext, string? workingDirectory,
        VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var hasGitChanges = false;
        var job = await dbContext.Jobs
            .Include(j => j.Project)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job != null)
        {
            var transition = JobStateMachine.TryTransition(job, status, errorMessage);
            if (!transition.Success)
            {
                _logger.LogWarning("Failed to complete job {JobId}: {Error}", jobId, transition.ErrorMessage);
                return false;
            }

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
                    // Brief delay to let git release file locks after the agent process exits
                    await Task.Delay(750, cancellationToken);

                    var baseCommit = executionContext.GitCommitBefore;
                    var (gitDiff, commitLog) = await CaptureGitDiffWithRetryAsync(
                        workingDirectory, baseCommit, cancellationToken);

                    if (!string.IsNullOrEmpty(gitDiff))
                    {
                        job.GitDiff = gitDiff;
                        hasGitChanges = true;
                        _logger.LogInformation("Captured git diff for job {JobId}: {Length} chars", jobId, gitDiff.Length);

                        // Count changed files for the badge/toast
                        try
                        {
                            var changedFiles = await _versionControlService.GetChangedFilesAsync(workingDirectory, baseCommit, cancellationToken);
                            job.ChangedFilesCount = changedFiles.Count;
                            _logger.LogInformation("Job {JobId} changed {Count} file(s)", jobId, changedFiles.Count);
                        }
                        catch (Exception cfEx)
                        {
                            _logger.LogWarning(cfEx, "Failed to count changed files for job {JobId}", jobId);
                        }

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
                        job.ChangedFilesCount = 0;
                        _logger.LogDebug("No git changes detected for job {JobId}", jobId);
                    }

                    await TryRecordAgentCommitAsync(job, workingDirectory, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture git diff for job {JobId}", jobId);
                }

                // Perform auto-commit if configured and job completed successfully
                // Also auto-commit when IdeasAutoCommit is true (even if project-level AutoCommitMode is Off)
                if (status == JobStatus.Completed && ShouldProcessGitDelivery(job))
                {
                    // Run build/test verification before committing if enabled
                    var buildPassed = await VerifyBuildAsync(job, workingDirectory, cancellationToken);
                    if (buildPassed)
                    {
                        await PerformAutoCommitAsync(job, workingDirectory, cancellationToken);
                        await CreatePullRequestIfConfiguredAsync(job, workingDirectory, cancellationToken);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Skipping auto-commit and push for job {JobId} because build verification failed. " +
                            "Changes remain uncommitted in {WorkingDirectory} for manual review.",
                            job.Id, workingDirectory);
                    }
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return hasGitChanges;
    }

    private static async Task RecordProviderAttemptAsync(
        Guid jobId,
        Guid providerId,
        string providerName,
        string? modelId,
        int attemptOrder,
        string reason,
        VibeSwarmDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var attempt = await dbContext.JobProviderAttempts
            .FirstOrDefaultAsync(a => a.JobId == jobId && a.AttemptOrder == attemptOrder, cancellationToken);

        if (attempt != null)
        {
            attempt.ProviderId = providerId;
            attempt.ProviderName = providerName;
            attempt.ModelId = modelId;
            attempt.Reason = reason;
            attempt.AttemptedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        dbContext.JobProviderAttempts.Add(new JobProviderAttempt
        {
            JobId = jobId,
            ProviderId = providerId,
            ProviderName = providerName,
            ModelId = modelId,
            AttemptOrder = attemptOrder,
            Reason = reason,
            WasSuccessful = false,
            AttemptedAt = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpdateProviderAttemptOutcomeAsync(
        Guid jobId,
        int attemptOrder,
        bool wasSuccessful,
        string? modelId,
        VibeSwarmDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var attempt = await dbContext.JobProviderAttempts
            .FirstOrDefaultAsync(a => a.JobId == jobId && a.AttemptOrder == attemptOrder, cancellationToken);

        if (attempt == null)
        {
            return;
        }

        attempt.WasSuccessful = wasSuccessful;
        attempt.ModelId = modelId ?? attempt.ModelId;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(string? GitDiff, IReadOnlyList<string>? CommitLog)> CaptureGitDiffWithRetryAsync(
        string workingDirectory, string? baseCommit,
        CancellationToken cancellationToken, int maxAttempts = 2)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string? gitDiff = null;
            IReadOnlyList<string>? commitLog = null;

            if (!string.IsNullOrEmpty(baseCommit))
            {
                var committedDiff = await _versionControlService.GetCommitRangeDiffAsync(workingDirectory, baseCommit, null, cancellationToken);

                commitLog = await _versionControlService.GetCommitLogAsync(workingDirectory, baseCommit, null, cancellationToken);
                if (commitLog.Count > 0)
                {
                    _logger.LogInformation("Found {Count} commits since base commit {BaseCommit}", commitLog.Count, baseCommit);
                }

                var uncommittedDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, null, cancellationToken);

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

                // Fallback: if both were empty, try a single diff from baseCommit against working tree
                if (string.IsNullOrEmpty(gitDiff))
                {
                    _logger.LogInformation("Committed and uncommitted diffs both empty, trying fallback diff from {BaseCommit} against working tree (attempt {Attempt}/{Max})",
                        baseCommit, attempt, maxAttempts);
                    gitDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, baseCommit, cancellationToken);
                }
            }
            else
            {
                gitDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, null, cancellationToken);
            }

            if (!string.IsNullOrEmpty(gitDiff))
            {
                return (gitDiff, commitLog);
            }

            if (attempt < maxAttempts)
            {
                _logger.LogInformation("Git diff capture returned empty on attempt {Attempt}/{Max}, retrying after delay", attempt, maxAttempts);
                await Task.Delay(1000, cancellationToken);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Performs auto-commit (and optionally push) based on project settings.
    /// </summary>
    private async Task PerformAutoCommitAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var shouldCreatePullRequest = ShouldCreatePullRequest(job);

            // Check if there are uncommitted changes
            var hasChanges = await _versionControlService.HasUncommittedChangesAsync(workingDirectory, cancellationToken);
            if (!hasChanges)
            {
                // No uncommitted changes — the agent may have committed changes itself.
                // If the HEAD has moved since the job started, record the current HEAD hash
                // so the UI knows the changes are committed.
                if (!string.IsNullOrEmpty(job.GitCommitBefore))
                {
                    var currentHash = await _versionControlService.GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
                    if (!string.IsNullOrEmpty(currentHash) &&
                        !string.Equals(currentHash, job.GitCommitBefore, StringComparison.OrdinalIgnoreCase))
                    {
                        job.GitCommitHash = currentHash;
                        _logger.LogInformation(
                            "Agent already committed changes for job {JobId}. Recorded HEAD {CommitHash} as GitCommitHash.",
                            job.Id, currentHash[..Math.Min(8, currentHash.Length)]);

                        // Determine effective commit mode: use project setting, or default to CommitOnly for IdeasAutoCommit
                        var effectiveMode = shouldCreatePullRequest
                            ? AutoCommitMode.CommitAndPush
                            : job.Project!.AutoCommitMode != AutoCommitMode.Off
                            ? job.Project.AutoCommitMode
                            : AutoCommitMode.CommitOnly;

                        // Push if configured
                        if (effectiveMode == AutoCommitMode.CommitAndPush)
                        {
                            var pushResult = await _versionControlService.PushAsync(workingDirectory, cancellationToken: cancellationToken);
                            if (pushResult.Success)
                            {
                                _logger.LogInformation("Auto-pushed agent-committed changes for job {JobId}", job.Id);
                            }
                            else
                            {
                                _logger.LogWarning("Auto-push failed for job {JobId}: {Error}. Changes were committed but not pushed.",
                                    job.Id, pushResult.Error);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No uncommitted or committed changes to auto-commit for job {JobId}", job.Id);
                    }
                }
                else
                {
                    _logger.LogDebug("No uncommitted changes to auto-commit for job {JobId}", job.Id);
                }
                return;
            }

            // Determine effective commit mode: use project setting, or default to CommitOnly for IdeasAutoCommit
            var effectiveCommitMode = shouldCreatePullRequest
                ? AutoCommitMode.CommitAndPush
                : job.Project!.AutoCommitMode != AutoCommitMode.Off
                ? job.Project.AutoCommitMode
                : AutoCommitMode.CommitOnly;

            var commitMessage = job.SessionSummary ?? $"{AppConstants.AppName}: {job.Title ?? "Job completed"}";

            _logger.LogInformation("Auto-committing changes for job {JobId} with mode {Mode}",
                job.Id, effectiveCommitMode);

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
                if (effectiveCommitMode == AutoCommitMode.CommitAndPush)
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
    /// Runs the project's configured build and test commands to verify the agent's changes compile and pass tests.
    /// Returns true if verification passed (or was not enabled), false if the build/tests failed.
    /// </summary>
    private async Task<bool> VerifyBuildAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        var project = job.Project;
        if (project == null || !project.BuildVerificationEnabled || string.IsNullOrWhiteSpace(project.BuildCommand))
        {
            return true;
        }

        var outputBuilder = new StringBuilder();

        try
        {
            _logger.LogInformation("Running build verification for job {JobId} in {WorkingDirectory}", job.Id, workingDirectory);

            // Run build command
            var buildResult = await RunShellCommandAsync(project.BuildCommand.Trim(), workingDirectory, cancellationToken);
            outputBuilder.AppendLine($"=== Build Command: {project.BuildCommand.Trim()} ===");
            outputBuilder.AppendLine($"Exit Code: {buildResult.ExitCode}");
            if (!string.IsNullOrWhiteSpace(buildResult.Output))
            {
                outputBuilder.AppendLine(buildResult.Output);
            }
            if (!string.IsNullOrWhiteSpace(buildResult.Error))
            {
                outputBuilder.AppendLine(buildResult.Error);
            }

            if (buildResult.ExitCode != 0)
            {
                _logger.LogWarning("Build verification FAILED for job {JobId}. Build command exited with code {ExitCode}",
                    job.Id, buildResult.ExitCode);
                job.BuildVerified = false;
                job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
                return false;
            }

            _logger.LogInformation("Build command succeeded for job {JobId}", job.Id);

            // Run test command if configured
            if (!string.IsNullOrWhiteSpace(project.TestCommand))
            {
                var testResult = await RunShellCommandAsync(project.TestCommand.Trim(), workingDirectory, cancellationToken);
                outputBuilder.AppendLine();
                outputBuilder.AppendLine($"=== Test Command: {project.TestCommand.Trim()} ===");
                outputBuilder.AppendLine($"Exit Code: {testResult.ExitCode}");
                if (!string.IsNullOrWhiteSpace(testResult.Output))
                {
                    outputBuilder.AppendLine(testResult.Output);
                }
                if (!string.IsNullOrWhiteSpace(testResult.Error))
                {
                    outputBuilder.AppendLine(testResult.Error);
                }

                if (testResult.ExitCode != 0)
                {
                    _logger.LogWarning("Test verification FAILED for job {JobId}. Test command exited with code {ExitCode}",
                        job.Id, testResult.ExitCode);
                    job.BuildVerified = false;
                    job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
                    return false;
                }

                _logger.LogInformation("Test command succeeded for job {JobId}", job.Id);
            }

            job.BuildVerified = true;
            job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
            return true;
        }
        catch (OperationCanceledException)
        {
            outputBuilder.AppendLine("Build verification was cancelled.");
            job.BuildVerified = false;
            job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build verification encountered an error for job {JobId}", job.Id);
            outputBuilder.AppendLine($"Build verification error: {ex.Message}");
            job.BuildVerified = false;
            job.BuildOutput = TruncateBuildOutput(outputBuilder.ToString());
            return false;
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunShellCommandAsync(
        string command, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c {EscapeShellArgument(command)}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        // 5 minute timeout for build/test commands
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout — kill process
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, await outputTask, "Build verification timed out after 5 minutes.");
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string EscapeShellArgument(string argument)
    {
        // Wrap in single quotes, escaping any embedded single quotes
        return "'" + argument.Replace("'", "'\\''") + "'";
    }

    private static string TruncateBuildOutput(string output)
    {
        const int maxLength = 50_000;
        if (output.Length <= maxLength) return output;
        return output[..(maxLength - 100)] + "\n\n... [output truncated] ...";
    }

    private async Task PrepareWorkingBranchAsync(Job job, string workingDirectory, string? checkpointBaseBranch, CancellationToken cancellationToken)
    {
        var sourceBranch = string.IsNullOrWhiteSpace(job.Branch)
            ? (string.IsNullOrWhiteSpace(checkpointBaseBranch) ? null : checkpointBaseBranch.Trim())
            : job.Branch.Trim();
        var targetBranch = GetEffectiveTargetBranch(job);

        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            _logger.LogInformation("Syncing current branch before job {JobId} execution", job.Id);
            var syncResult = await _versionControlService.SyncWithOriginAsync(workingDirectory, cancellationToken: cancellationToken);
            if (!syncResult.Success)
            {
                _logger.LogWarning("Failed to sync current branch before job {JobId}: {Error}", job.Id, syncResult.Error);
            }
            return;
        }

        var branches = await _versionControlService.GetBranchesAsync(workingDirectory, includeRemote: true, cancellationToken);
        var sourceExists = BranchExists(branches, sourceBranch);

        if (sourceExists)
        {
            _logger.LogInformation("Checking out configured branch '{Branch}' for job {JobId}", sourceBranch, job.Id);
            var checkoutResult = await _versionControlService.HardCheckoutBranchAsync(workingDirectory, sourceBranch, cancellationToken: cancellationToken);
            if (!checkoutResult.Success)
            {
                _logger.LogWarning("Failed to checkout branch '{Branch}' for job {JobId}: {Error}", sourceBranch, job.Id, checkoutResult.Error);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(targetBranch) &&
            !string.Equals(targetBranch, sourceBranch, StringComparison.Ordinal) &&
            BranchExists(branches, targetBranch))
        {
            _logger.LogInformation("Using target branch '{TargetBranch}' as the base for new branch '{SourceBranch}' on job {JobId}", targetBranch, sourceBranch, job.Id);
            var baseCheckoutResult = await _versionControlService.HardCheckoutBranchAsync(workingDirectory, targetBranch, cancellationToken: cancellationToken);
            if (!baseCheckoutResult.Success)
            {
                _logger.LogWarning("Failed to checkout target branch '{Branch}' for job {JobId}: {Error}", targetBranch, job.Id, baseCheckoutResult.Error);
            }
        }
        else
        {
            var syncResult = await _versionControlService.SyncWithOriginAsync(workingDirectory, cancellationToken: cancellationToken);
            if (!syncResult.Success)
            {
                _logger.LogWarning("Failed to sync current branch before creating new branch '{Branch}' for job {JobId}: {Error}", sourceBranch, job.Id, syncResult.Error);
            }
        }

        var createResult = await _versionControlService.CreateBranchAsync(
            workingDirectory,
            sourceBranch,
            switchToBranch: true,
            cancellationToken: cancellationToken);
        if (!createResult.Success)
        {
            _logger.LogWarning("Failed to create job branch '{Branch}' for job {JobId}: {Error}", sourceBranch, job.Id, createResult.Error);
        }
    }

    private async Task<string?> PreserveWorkingTreeBeforeBranchPreparationAsync(
        Job job,
        string workingDirectory,
        VibeSwarmDbContext dbContext,
        bool captureJobDiff,
        string reason,
        CancellationToken cancellationToken)
    {
        var hasUncommittedChanges = await _versionControlService.HasUncommittedChangesAsync(workingDirectory, cancellationToken);
        if (!hasUncommittedChanges)
        {
            return null;
        }

        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Protecting);

        var originalBranch = await _versionControlService.GetCurrentBranchAsync(workingDirectory, cancellationToken);
        if (captureJobDiff)
        {
            job.GitDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, null, cancellationToken);
            var changedFiles = await _versionControlService.GetChangedFilesAsync(workingDirectory, null, cancellationToken);
            job.ChangedFilesCount = changedFiles.Count;
        }

        var recoveryBranch = BuildRecoveryBranchName(job.Id, originalBranch);
        var createBranchResult = await _versionControlService.CreateBranchAsync(
            workingDirectory,
            recoveryBranch,
            switchToBranch: true,
            cancellationToken: cancellationToken);

        if (!createBranchResult.Success)
        {
            job.GitCheckpointStatus = GitCheckpointStatus.None;
            throw new GitCheckpointRequiredException($"Unable to preserve local git changes before branch preparation: {createBranchResult.Error}");
        }

        var checkpointMessage = $"{AppConstants.AppName} checkpoint before job {job.Id.ToString("N")[..8]}";
        var commitMessage = string.IsNullOrWhiteSpace(originalBranch)
            ? checkpointMessage
            : $"{checkpointMessage} on {originalBranch}";
        var commitResult = await _versionControlService.CommitAllChangesAsync(workingDirectory, commitMessage, cancellationToken);
        if (!commitResult.Success)
        {
            job.GitCheckpointStatus = GitCheckpointStatus.None;
            throw new GitCheckpointRequiredException($"Unable to commit preserved local git changes before branch preparation: {commitResult.Error}");
        }

        job.GitCheckpointBranch = recoveryBranch;
        job.GitCheckpointBaseBranch = originalBranch;
        job.GitCheckpointCommitHash = commitResult.CommitHash;
        job.GitCheckpointReason = reason;
        job.GitCheckpointCapturedAt = DateTime.UtcNow;
        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Preserved);

        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Preserved local git changes for job {JobId} on recovery branch {RecoveryBranch} ({CommitHash}) before branch preparation",
            job.Id,
            recoveryBranch,
            commitResult.CommitHash?[..Math.Min(8, commitResult.CommitHash?.Length ?? 0)]);

        return originalBranch;
    }

    private async Task TryRecordAgentCommitAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(job.GitCommitHash))
        {
            return;
        }

        var hasChanges = await _versionControlService.HasUncommittedChangesAsync(workingDirectory, cancellationToken);
        if (hasChanges)
        {
            return;
        }

        var currentHash = await _versionControlService.GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(currentHash) ||
            string.Equals(currentHash, job.GitCommitBefore, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        job.GitCommitHash = currentHash;
        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Cleared);
        _logger.LogInformation(
            "Recorded self-committed agent output for job {JobId} at {CommitHash}",
            job.Id,
            currentHash[..Math.Min(8, currentHash.Length)]);
    }

    private static string BuildRecoveryBranchName(Guid jobId, string? originalBranch)
    {
        var branchSlug = string.IsNullOrWhiteSpace(originalBranch) ? "detached" : SanitizeBranchSegment(originalBranch);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return $"vibeswarm/recovery/{branchSlug}-{timestamp}-{jobId.ToString("N")[..8]}";
    }

    private static string SanitizeBranchSegment(string branchName)
    {
        var sanitized = Regex.Replace(branchName.Trim().ToLowerInvariant(), @"[^a-z0-9/_-]+", "-");
        sanitized = sanitized.Replace("//", "/").Trim('-', '/');
        return string.IsNullOrWhiteSpace(sanitized) ? "branch" : sanitized;
    }

    private async Task CreatePullRequestIfConfiguredAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!ShouldCreatePullRequest(job) || !string.IsNullOrWhiteSpace(job.PullRequestUrl))
        {
            return;
        }

        var targetBranch = GetEffectiveTargetBranch(job);
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            _logger.LogWarning("Job {JobId} requested pull-request delivery but no target branch was configured.", job.Id);
            return;
        }

        var sourceBranch = string.IsNullOrWhiteSpace(job.Branch)
            ? await _versionControlService.GetCurrentBranchAsync(workingDirectory, cancellationToken)
            : job.Branch;

        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            _logger.LogWarning("Job {JobId} requested pull-request delivery but the current branch could not be determined.", job.Id);
            return;
        }

        if (string.Equals(sourceBranch, targetBranch, StringComparison.Ordinal))
        {
            _logger.LogWarning("Job {JobId} requested pull-request delivery but source and target branches are both '{Branch}'.", job.Id, sourceBranch);
            return;
        }

        var pullRequestTitle = job.SessionSummary ?? $"{AppConstants.AppName}: {job.Title ?? "Job completed"}";
        var pullRequestBody = BuildPullRequestBody(job, sourceBranch, targetBranch);
        var pullRequestResult = await _versionControlService.CreatePullRequestAsync(
            workingDirectory,
            sourceBranch,
            targetBranch,
            pullRequestTitle,
            pullRequestBody,
            cancellationToken);

        if (!pullRequestResult.Success)
        {
            _logger.LogWarning("Failed to create pull request for job {JobId}: {Error}", job.Id, pullRequestResult.Error);
            return;
        }

        job.PullRequestNumber = pullRequestResult.PullRequestNumber;
        job.PullRequestUrl = pullRequestResult.PullRequestUrl;
        job.PullRequestCreatedAt = DateTime.UtcNow;
        _logger.LogInformation("Created pull request for job {JobId}: {PullRequestUrl}", job.Id, job.PullRequestUrl);
    }

    private static bool BranchExists(IReadOnlyList<GitBranchInfo> branches, string branchName)
    {
        return branches.Any(branch =>
            string.Equals(branch.Name, branchName, StringComparison.Ordinal) ||
            string.Equals(branch.ShortName, branchName, StringComparison.Ordinal));
    }

    private static bool ShouldProcessGitDelivery(Job job)
    {
        return job.Project?.AutoCommitMode != AutoCommitMode.Off
            || job.Project?.IdeasAutoCommit == true
            || ShouldCreatePullRequest(job);
    }

    private static bool ShouldCreatePullRequest(Job job)
    {
        return job.GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest
            && !string.IsNullOrWhiteSpace(GetEffectiveTargetBranch(job));
    }

    private static string? GetEffectiveTargetBranch(Job job)
    {
        return string.IsNullOrWhiteSpace(job.TargetBranch)
            ? string.IsNullOrWhiteSpace(job.Project?.DefaultTargetBranch) ? null : job.Project.DefaultTargetBranch.Trim()
            : job.TargetBranch.Trim();
    }

    private static string BuildPullRequestBody(Job job, string sourceBranch, string targetBranch)
    {
        var body = new StringBuilder();
        body.AppendLine("## VibeSwarm Job");
        body.AppendLine();
        body.AppendLine($"- Source branch: `{sourceBranch}`");
        body.AppendLine($"- Target branch: `{targetBranch}`");
        body.AppendLine($"- Job: `{job.Title ?? job.GoalPrompt}`");
        body.AppendLine();
        body.AppendLine("### Goal");
        body.AppendLine(job.GoalPrompt.Trim());

        if (!string.IsNullOrWhiteSpace(job.SessionSummary))
        {
            body.AppendLine();
            body.AppendLine("### Session Summary");
            body.AppendLine(job.SessionSummary.Trim());
        }

        return body.ToString().Trim();
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
            "thinking" => MessageRole.System,
            "reasoning" => MessageRole.System,
            "reasoning_summary" => MessageRole.System,
            "plan" => MessageRole.System,
            "tool_use" => MessageRole.ToolUse,
            "tool_result" => MessageRole.ToolResult,
            "tool_error" => MessageRole.ToolResult,
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

        TriggerProcessing();
    }

    /// <summary>
    /// Gets the MCP config file path for the given provider.
    /// Generates a temporary MCP config file containing all enabled skills.
    /// </summary>
    private async Task<(string? McpConfigPath, List<string>? AdditionalArgs)> GetMcpExecutionOptionsAsync(
        Guid providerId,
        Project? project,
        string? workingDirectory,
        CancellationToken cancellationToken)
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
                return (null, null);
            }

            var mcpConfigPath = await mcpConfigService.GenerateMcpConfigFileAsync(
                provider.Type,
                project,
                workingDirectory,
                cancellationToken);
            if (!string.IsNullOrEmpty(mcpConfigPath))
            {
                _logger.LogDebug("Generated MCP config at {ConfigPath} for provider {ProviderId}", mcpConfigPath, providerId);
            }

            return (mcpConfigPath, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate MCP config for provider {ProviderId}", providerId);
            return (null, null);
        }
    }

    private async Task<string?> PrepareProjectMemoryFileAsync(Project? project, CancellationToken cancellationToken)
    {
        if (project == null)
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var projectMemoryService = scope.ServiceProvider.GetRequiredService<IProjectMemoryService>();
        return await projectMemoryService.PrepareMemoryFileAsync(project, cancellationToken);
    }

    private async Task PersistProjectMemoryAsync(Guid? projectId, string? projectMemoryFilePath, CancellationToken cancellationToken)
    {
        if (!projectId.HasValue || string.IsNullOrWhiteSpace(projectMemoryFilePath))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var projectMemoryService = scope.ServiceProvider.GetRequiredService<IProjectMemoryService>();
        await projectMemoryService.SyncMemoryFromFileAsync(projectId.Value, projectMemoryFilePath, cancellationToken);
    }

    private async Task NotifyJobGitDiffUpdatedAsync(Guid jobId, bool hasChanges)
    {
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobGitDiffUpdated(jobId, hasChanges);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send git diff notification for job {JobId}", jobId);
            }
        }
    }
}
