using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService : BackgroundService
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
    // Coalescing wake-up channel: bounded to 1 and drops newest writes so repeated triggers
    // collapse into a single pending signal. The polling tick serves as a safety fallback for
    // orphan recovery and for jobs whose NotBeforeUtc has passed without an explicit trigger.
    private readonly Channel<byte> _wakeSignal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
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
        /// Most recent provider session ID observed during execution.
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Prompt snapshot currently being executed.
        /// Persisted to durable recovery checkpoints.
        /// </summary>
        public string? ActivePrompt { get; set; }

        /// <summary>
        /// Most recent activity summary observed from the provider.
        /// </summary>
        public string? LatestActivity { get; set; }

        /// <summary>
        /// Last time durable recovery state was flushed to the database.
        /// </summary>
        public DateTime LastCheckpointPersistedAt { get; set; }

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

            // Wait for either the polling interval or a wake signal from the Channel.
            try
            {
                using var timeoutCts = new CancellationTokenSource(_pollingInterval);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
                try
                {
                    await _wakeSignal.Reader.ReadAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // Polling tick — safety fallback for orphan recovery / rate-limited jobs becoming eligible.
                }
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
                .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Started || j.Status == JobStatus.Planning || j.Status == JobStatus.Processing)
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
            // (Started/Planning/Processing with old heartbeats, excluding already-completed jobs)
            var cutoffTime = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            var orphanedJobs = await dbContext.Jobs
                .Include(j => j.Project)
                .Where(j => (j.Status == JobStatus.Pending || j.Status == JobStatus.Started || j.Status == JobStatus.Planning || j.Status == JobStatus.Processing))
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
                    JobRecoveryHelper.CaptureRecoveryState(
                        job,
                        job.Status == JobStatus.Planning ? JobStatus.Planning : JobStatus.Processing,
                        job.RecoveryPrompt ?? job.GoalPrompt,
                        job.SessionId,
                        job.ConsoleOutput);
                    JobStateMachine.TryTransition(job, JobStatus.New, "Automatic orphan recovery");
                    job.RetryCount++;
                    job.ErrorMessage = "Worker crashed or became unresponsive. Automatic recovery.";
                }
                else
                {
                    JobRecoveryHelper.ClearRecoveryState(job);
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
    /// Triggers immediate job processing. Safe to call from any thread.
    /// Repeated calls between ticks coalesce into a single pending wake (bounded channel, DropWrite).
    /// </summary>
    public void TriggerProcessing()
    {
        _wakeSignal.Writer.TryWrite(0);
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
                    var scopedSkillStorage = jobScope.ServiceProvider.GetRequiredService<ISkillStorageService>();

                    await ProcessJobAsync(job, scopedJobService, scopedProviderService, scopedDbContext, scopedSkillStorage, context, jobCts.Token);
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
                .SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.WorkerInstanceId, _workerInstanceId)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? claimTime)
                .SetProperty(j => j.LastActivityAt, claimTime)
                .SetProperty(j => j.LastHeartbeatAt, claimTime)
                .SetProperty(j => j.NotBeforeUtc, (DateTime?)null),
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
}
