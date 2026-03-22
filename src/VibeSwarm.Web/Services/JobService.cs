using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

public class JobService : IJobService
{
    private const int DefaultJobsPageSize = 25;
    private const int DefaultProjectJobsPageSize = 10;
    private const int MaxPageSize = 100;

    private readonly VibeSwarmDbContext _dbContext;
    private readonly IJobUpdateService? _jobUpdateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly JobProcessingService? _jobProcessingService;

    public JobService(
        VibeSwarmDbContext dbContext,
        IServiceProvider serviceProvider,
        IJobUpdateService? jobUpdateService = null,
        JobProcessingService? jobProcessingService = null)
    {
        _dbContext = dbContext;
        _serviceProvider = serviceProvider;
        _jobUpdateService = jobUpdateService;
        _jobProcessingService = jobProcessingService;
    }

    public async Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
                .ThenInclude(p => p!.Environments)
            .Include(j => j.Provider)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = DefaultJobsPageSize, CancellationToken cancellationToken = default)
    {
        var normalizedPageSize = NormalizePageSize(pageSize, DefaultJobsPageSize);
        var filteredQuery = BuildFilteredJobsQuery(projectId, statusFilter);
        var totalCount = await filteredQuery.CountAsync(cancellationToken);
        var normalizedPage = NormalizePageNumber(page, normalizedPageSize, totalCount);

        var aggregates = totalCount == 0
            ? null
            : await filteredQuery
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    TotalInputTokens = group.Sum(job => job.InputTokens ?? 0),
                    TotalOutputTokens = group.Sum(job => job.OutputTokens ?? 0),
                    TotalCostUsd = group.Sum(job => job.TotalCostUsd ?? 0m)
                })
                .FirstOrDefaultAsync(cancellationToken);

        return new JobsListResult
        {
            PageNumber = normalizedPage,
            PageSize = normalizedPageSize,
            TotalCount = totalCount,
            TotalInputTokens = aggregates?.TotalInputTokens ?? 0,
            TotalOutputTokens = aggregates?.TotalOutputTokens ?? 0,
            TotalCostUsd = aggregates?.TotalCostUsd ?? 0m,
            ProjectCounts = await _dbContext.Jobs
                .GroupBy(job => job.ProjectId)
                .Select(group => new JobProjectCountSummary
                {
                    ProjectId = group.Key,
                    TotalCount = group.Count(),
                     ActiveCount = group.Count(job =>
                         job.Status == JobStatus.New ||
                         job.Status == JobStatus.Started ||
                         job.Status == JobStatus.Planning ||
                         job.Status == JobStatus.Processing)
                 })
                .ToListAsync(cancellationToken),
            Items = await filteredQuery
                .Include(j => j.Project)
                    .ThenInclude(p => p!.Environments)
                .Include(j => j.Provider)
                .OrderByDescending(j => j.CreatedAt)
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .ToListAsync(cancellationToken)
        };
    }

    public async Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Provider)
            .Where(j => j.ProjectId == projectId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = DefaultProjectJobsPageSize, CancellationToken cancellationToken = default)
    {
        var normalizedPageSize = NormalizePageSize(pageSize, DefaultProjectJobsPageSize);
        var baseQuery = _dbContext.Jobs
            .Where(j => j.ProjectId == projectId);
        var totalCount = await baseQuery.CountAsync(cancellationToken);
        var normalizedPage = NormalizePageNumber(page, normalizedPageSize, totalCount);

        return new ProjectJobsListResult
        {
            PageNumber = normalizedPage,
            PageSize = normalizedPageSize,
            TotalCount = totalCount,
            ActiveCount = await baseQuery.CountAsync(j =>
                j.Status == JobStatus.New ||
                j.Status == JobStatus.Pending ||
                j.Status == JobStatus.Started ||
                j.Status == JobStatus.Planning ||
                j.Status == JobStatus.Processing ||
                j.Status == JobStatus.Paused, cancellationToken),
            CompletedCount = await baseQuery.CountAsync(j => j.Status == JobStatus.Completed, cancellationToken),
            Items = await baseQuery
                .Include(j => j.Provider)
                .OrderByDescending(j => j.CreatedAt)
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .ToListAsync(cancellationToken)
        };
    }

    public async Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default)
    {
        // Get projects that already have an in-flight job. Queued "New" jobs are handled below
        // so only the next eligible job per project is returned.
        var projectsWithRunningJobs = await _dbContext.Jobs
            .Where(j => j.Status == JobStatus.Pending
                || j.Status == JobStatus.Started
                || j.Status == JobStatus.Planning
                || j.Status == JobStatus.Processing
                || j.Status == JobStatus.Paused
                || j.Status == JobStatus.Stalled)
            .Select(j => j.ProjectId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var pendingJobs = await _dbContext.Jobs
            .Include(j => j.Project)
                .ThenInclude(p => p!.Environments)
            .Include(j => j.Provider)
            .Where(j => j.Status == JobStatus.New && !j.CancellationRequested)
            .Where(j => !projectsWithRunningJobs.Contains(j.ProjectId))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .ToListAsync(cancellationToken);

        // Return only the next queued job per project so dispatchers cannot start multiple
        // jobs for the same repository in the same scheduling pass.
        return pendingJobs
            .GroupBy(j => j.ProjectId)
            .Select(group => group.First())
            .ToList();
    }

    public async Task<IEnumerable<Job>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
                .ThenInclude(p => p!.Environments)
            .Include(j => j.Provider)
            .Where(j => j.Status == JobStatus.Started || j.Status == JobStatus.Planning || j.Status == JobStatus.Processing || j.Status == JobStatus.New)
            .OrderByDescending(j => j.StartedAt ?? j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
                .ThenInclude(p => p!.Environments)
            .Include(j => j.Provider)
            .Include(j => j.ProviderAttempts.OrderBy(a => a.AttemptOrder))
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
                .ThenInclude(p => p!.Environments)
            .Include(j => j.Provider)
            .Include(j => j.ProviderAttempts.OrderBy(a => a.AttemptOrder))
            .Include(j => j.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default)
    {
        NormalizeJobForPersistence(job);
        await ValidateRequestedExecutionAsync(job, cancellationToken);

        job.Id = Guid.NewGuid();
        job.CreatedAt = DateTime.UtcNow;
        job.Status = JobStatus.New;
        job.ActiveExecutionIndex = 0;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;
        job.ProviderAttempts.Clear();

        await InitializeExecutionPlanAsync(job, cancellationToken);
        if (job.ProviderId == Guid.Empty)
        {
            throw new InvalidOperationException("No enabled providers are available for this job.");
        }

        _dbContext.Jobs.Add(job);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify that a new job was created (so processing can start immediately)
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobCreated(job.Id, job.ProjectId);
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
            }
            catch
            {
                // Don't fail job creation if notification fails
            }
        }

        _jobProcessingService?.TriggerProcessing();

        return job;
    }

    public async Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            throw new InvalidOperationException($"Job with ID {id} not found.");
        }

        job.Status = status;

        if (status == JobStatus.Started)
        {
            job.StartedAt = DateTime.UtcNow;
        }

        if (status == JobStatus.Completed || status == JobStatus.Failed || status == JobStatus.Cancelled)
        {
            job.CompletedAt = DateTime.UtcNow;
            // Calculate and store execution duration
            if (job.StartedAt.HasValue)
            {
                job.ExecutionDurationSeconds = (job.CompletedAt.Value - job.StartedAt.Value).TotalSeconds;
            }
        }

        if (output != null)
        {
            job.Output = output;
        }

        if (errorMessage != null)
        {
            job.ErrorMessage = errorMessage;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return job;
    }

    public async Task<Job> UpdateJobResultAsync(
        Guid id,
        JobStatus status,
        string? sessionId,
        string? output,
        string? errorMessage,
        int? inputTokens,
        int? outputTokens,
        decimal? costUsd,
        CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            throw new InvalidOperationException($"Job with ID {id} not found.");
        }

        job.Status = status;

        if (status == JobStatus.Started)
        {
            job.StartedAt = DateTime.UtcNow;
        }

        if (status == JobStatus.Completed || status == JobStatus.Failed || status == JobStatus.Cancelled)
        {
            job.CompletedAt = DateTime.UtcNow;
            // Calculate and store execution duration
            if (job.StartedAt.HasValue)
            {
                job.ExecutionDurationSeconds = (job.CompletedAt.Value - job.StartedAt.Value).TotalSeconds;
            }
            // Clear progress tracking when job reaches terminal state
            job.CurrentActivity = null;
            job.LastActivityAt = DateTime.UtcNow;
        }

        if (sessionId != null)
        {
            job.SessionId = sessionId;
        }

        if (output != null)
        {
            job.Output = output;
        }

        if (errorMessage != null)
        {
            job.ErrorMessage = errorMessage;
        }

        if (inputTokens.HasValue)
        {
            job.InputTokens = inputTokens;
        }

        if (outputTokens.HasValue)
        {
            job.OutputTokens = outputTokens;
        }

        if (costUsd.HasValue)
        {
            job.TotalCostUsd = costUsd;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return job;
    }

    public async Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default)
    {
        message.Id = Guid.NewGuid();
        message.JobId = jobId;
        message.CreatedAt = DateTime.UtcNow;

        _dbContext.JobMessages.Add(message);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default)
    {
        foreach (var message in messages)
        {
            message.Id = Guid.NewGuid();
            message.JobId = jobId;
            if (message.CreatedAt == default)
            {
                message.CreatedAt = DateTime.UtcNow;
            }
            _dbContext.JobMessages.Add(message);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only allow cancellation for jobs that are not already completed
        if (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed || job.Status == JobStatus.Cancelled)
        {
            return false;
        }

        job.CancellationRequested = true;
        job.LastActivityAt = DateTime.UtcNow; // Update activity time to track when cancellation was requested

        // If job hasn't started yet, mark it as cancelled immediately
        if (job.Status == JobStatus.New || job.Status == JobStatus.Pending)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.CurrentActivity = null;
            job.WorkerInstanceId = null;
            job.ProcessId = null;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return true;
    }

    public async Task<bool> ForceCancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only force cancel jobs that are actively running
        if (job.Status != JobStatus.Started && job.Status != JobStatus.Planning && job.Status != JobStatus.Processing)
        {
            return false;
        }

        // Try to kill the process if we have a PID
        if (job.ProcessId.HasValue)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(job.ProcessId.Value);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Process already exited
            }
            catch (Exception)
            {
                // Ignore errors killing process
            }
        }

        // Force the job to cancelled state
        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTime.UtcNow;
        job.CancellationRequested = true;
        job.CurrentActivity = null;
        job.WorkerInstanceId = null;
        job.ProcessId = null;
        job.ErrorMessage = "Job was force-cancelled by user.";

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about completion
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobCompleted(job.Id, false, job.ErrorMessage);
            }
            catch { }
        }

        // Handle idea state when job is force-cancelled
        try
        {
            var ideaService = _serviceProvider.GetService<IIdeaService>();
            if (ideaService != null)
            {
                await ideaService.HandleJobCompletionAsync(job.Id, false, cancellationToken);
            }
        }
        catch { /* Ignore errors handling idea state */ }

        return true;
    }

    public async Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        return job?.CancellationRequested ?? false;
    }

    public async Task UpdateProgressAsync(Guid id, string? currentActivity, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return;
        }

        job.CurrentActivity = currentActivity;
        job.LastActivityAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ResetJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Do not allow resetting completed jobs
        if (job.Status == JobStatus.Completed)
        {
            return false;
        }

        // Reset job to initial state while preserving the original configuration
        job.Status = JobStatus.New;
        job.CancellationRequested = false;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.Output = null;
        job.ErrorMessage = null;
        job.CurrentActivity = null;
        job.LastActivityAt = null;
        job.WorkerInstanceId = null;
        job.LastHeartbeatAt = null;
        job.ProcessId = null;
        job.ConsoleOutput = null;  // Clear accumulated console output
        job.GitDiff = null;        // Clear git diff
        job.GitCommitBefore = null; // Clear git commit reference
        job.GitCommitHash = null;  // Clear committed results hash
        job.PlanningOutput = null;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningGeneratedAt = null;
        job.GitCheckpointStatus = GitCheckpointStatus.None;
        job.GitCheckpointBranch = null;
        job.GitCheckpointBaseBranch = null;
        job.GitCheckpointCommitHash = null;
        job.GitCheckpointReason = null;
        job.GitCheckpointCapturedAt = null;
        // Keep SessionId for potential session continuation
        // Keep InputTokens, OutputTokens, TotalCostUsd for historical tracking
        // Keep Messages for audit trail and context

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return true;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job != null)
        {
            var projectId = job.ProjectId;
            _dbContext.Jobs.Remove(job);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Notify about job deletion
            if (_jobUpdateService != null)
            {
                try
                {
                    await _jobUpdateService.NotifyJobDeleted(id, projectId);
                }
                catch { }
            }
        }
    }

    public async Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        job.GitCommitHash = commitHash;
        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Cleared);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        job.GitDiff = gitDiff;
        job.ChangedFilesCount = string.IsNullOrWhiteSpace(gitDiff)
            ? 0
            : GitDiffParser.ParseDiff(gitDiff).Count;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about git diff update
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobGitDiffUpdated(job.Id, !string.IsNullOrEmpty(gitDiff));
            }
            catch { }
        }

        return true;
    }

    public async Task<bool> UpdateGitDeliveryAsync(
        Guid id,
        string? commitHash = null,
        int? pullRequestNumber = null,
        string? pullRequestUrl = null,
        DateTime? pullRequestCreatedAt = null,
        DateTime? mergedAt = null,
        CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(commitHash))
        {
            job.GitCommitHash = commitHash.Trim();
            JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Cleared);
        }

        if (pullRequestNumber.HasValue)
        {
            job.PullRequestNumber = pullRequestNumber;
        }

        if (!string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            job.PullRequestUrl = pullRequestUrl.Trim();
        }

        if (pullRequestCreatedAt.HasValue)
        {
            job.PullRequestCreatedAt = pullRequestCreatedAt;
        }

        if (mergedAt.HasValue)
        {
            job.MergedAt = mergedAt;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType,
        string? choices = null, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only allow pausing jobs that are currently processing
        if (job.Status != JobStatus.Processing && job.Status != JobStatus.Planning && job.Status != JobStatus.Started)
        {
            return false;
        }

        job.Status = JobStatus.Paused;
        job.PendingInteractionPrompt = interactionPrompt;
        job.InteractionType = interactionType;
        job.InteractionChoices = choices;
        job.InteractionRequestedAt = DateTime.UtcNow;
        job.CurrentActivity = "Waiting for user input...";
        job.LastActivityAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change and interaction request
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());

                // Parse choices if provided
                List<string>? choicesList = null;
                if (!string.IsNullOrEmpty(choices))
                {
                    try
                    {
                        choicesList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(choices);
                    }
                    catch { }
                }

                await _jobUpdateService.NotifyJobInteractionRequired(job.Id, interactionPrompt, interactionType,
                    choicesList, null);
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        return true;
    }

    public async Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id,
        CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null || job.Status != JobStatus.Paused)
        {
            return null;
        }

        return (job.PendingInteractionPrompt, job.InteractionType, job.InteractionChoices);
    }

    public async Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .Include(j => j.Project)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only allow resuming paused jobs
        if (job.Status != JobStatus.Paused)
        {
            return false;
        }

        job.Status = job.Project?.PlanningEnabled == true &&
            job.Project.PlanningProviderId.HasValue &&
            string.IsNullOrWhiteSpace(job.PlanningOutput)
            ? JobStatus.Planning
            : JobStatus.Processing;
        job.PendingInteractionPrompt = null;
        job.InteractionType = null;
        job.InteractionChoices = null;
        job.InteractionRequestedAt = null;
        job.CurrentActivity = "Resuming...";
        job.LastActivityAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change and resume
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                await _jobUpdateService.NotifyJobResumed(job.Id);
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        return true;
    }

    public async Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken cancellationToken = default)
    {
        var trimmedFollowUp = followUpPrompt?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedFollowUp))
        {
            return false;
        }

        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null || job.Status != JobStatus.Completed)
        {
            return false;
        }

        var submittedAt = DateTime.UtcNow;
        var message = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Role = MessageRole.User,
            Content = trimmedFollowUp,
            CreatedAt = submittedAt
        };

        _dbContext.JobMessages.Add(message);

        job.GoalPrompt = BuildContinuationPrompt(job.GoalPrompt, trimmedFollowUp);
        await ResetJobForFollowUp(job, submittedAt, cancellationToken);
        await InitializeExecutionPlanAsync(job, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return true;
    }

    public async Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
            .Include(j => j.Provider)
            .Where(j => j.Status == JobStatus.Paused)
            .OrderByDescending(j => j.InteractionRequestedAt ?? j.LastActivityAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId && j.ProviderId == providerId && j.ModelUsed != null)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => j.ModelUsed)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Do not allow resetting completed jobs
        if (job.Status == JobStatus.Completed)
        {
            return false;
        }

        // If provider is being changed, verify it exists and is enabled
        if (providerId.HasValue && providerId.Value != job.ProviderId)
        {
            var provider = await _dbContext.Providers
                .FirstOrDefaultAsync(p => p.Id == providerId.Value && p.IsEnabled, cancellationToken);
            if (provider == null)
            {
                return false;
            }
            job.ProviderId = providerId.Value;
        }

        // Reset job to initial state while preserving the original configuration
        job.Status = JobStatus.New;
        job.CancellationRequested = false;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.Output = null;
        job.ErrorMessage = null;
        job.CurrentActivity = null;
        job.LastActivityAt = null;
        job.WorkerInstanceId = null;
        job.LastHeartbeatAt = null;
        job.ProcessId = null;
        job.ConsoleOutput = null;
        job.GitDiff = null;
        job.GitCommitBefore = null;
        job.GitCommitHash = null;
        job.GitCheckpointStatus = GitCheckpointStatus.None;
        job.GitCheckpointBranch = null;
        job.GitCheckpointBaseBranch = null;
        job.GitCheckpointCommitHash = null;
        job.GitCheckpointReason = null;
        job.GitCheckpointCapturedAt = null;
        job.PullRequestNumber = null;
        job.PullRequestUrl = null;
        job.PullRequestCreatedAt = null;
        job.MergedAt = null;
        job.PlanningOutput = null;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningGeneratedAt = null;
        job.SessionId = null; // Clear session for fresh start with potentially new provider
        job.ModelUsed = modelId; // Set the requested model (null means use provider default)
        job.RetryCount++; // Increment retry count to track attempts
        job.ActiveExecutionIndex = 0;
        job.ExecutionPlan = null;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;

        // Clear token/cost tracking for fresh run
        job.InputTokens = null;
        job.OutputTokens = null;
        job.TotalCostUsd = null;

        var attempts = await _dbContext.JobProviderAttempts
            .Where(a => a.JobId == job.Id)
            .ToListAsync(cancellationToken);
        if (attempts.Count > 0)
        {
            _dbContext.JobProviderAttempts.RemoveRange(attempts);
        }

        await InitializeExecutionPlanAsync(job, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return true;
    }

    public async Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null || JobStateMachine.IsTerminalState(job.Status))
        {
            return false;
        }

        job.Status = JobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        job.ErrorMessage = "Manually marked as failed by user.";
        job.CurrentActivity = null;
        job.WorkerInstanceId = null;
        job.ProcessId = null;
        job.LastHeartbeatAt = null;
        job.CancellationRequested = false;

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return true;
    }

    public async Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only allow updating prompt for jobs that haven't started yet or are in terminal states
        if (job.Status != JobStatus.New && job.Status != JobStatus.Failed && job.Status != JobStatus.Cancelled)
        {
            return false;
        }

        var shouldSyncTitle = JobTitleHelper.ShouldSyncTitleWithGoalPrompt(job.Title, job.GoalPrompt);
        job.GoalPrompt = newPrompt?.Trim() ?? string.Empty;
        job.Title = shouldSyncTitle
            ? JobTitleHelper.BuildSafeJobTitle(null, job.GoalPrompt)
            : JobTitleHelper.BuildSafeJobTitle(job.Title, job.GoalPrompt);
        job.PlanningOutput = null;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningGeneratedAt = null;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var activeStatuses = new[]
        {
            JobStatus.New, JobStatus.Pending, JobStatus.Started, JobStatus.Planning,
            JobStatus.Processing, JobStatus.Paused, JobStatus.Stalled
        };

        var jobs = await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId && activeStatuses.Contains(j.Status))
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        foreach (var job in jobs)
        {
            // Kill running processes
            if (job.ProcessId.HasValue && (job.Status == JobStatus.Started || job.Status == JobStatus.Planning || job.Status == JobStatus.Processing))
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(job.ProcessId.Value);
                    process.Kill(entireProcessTree: true);
                }
                catch { /* Process already exited or inaccessible */ }
            }

            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.CancellationRequested = true;
            job.CurrentActivity = null;
            job.WorkerInstanceId = null;
            job.ProcessId = null;
            job.ErrorMessage ??= "Cancelled in bulk by user.";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about cancellations
        if (_jobUpdateService != null)
        {
            foreach (var job in jobs)
            {
                try
                {
                    await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                }
                catch { }
            }
        }

        _jobProcessingService?.TriggerProcessing();

        return jobs.Count;
    }

    public async Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var jobs = await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId && j.Status == JobStatus.Completed)
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return 0;
        }

        var deletedJobs = jobs
            .Select(job => new { job.Id, job.ProjectId })
            .ToList();

        _dbContext.Jobs.RemoveRange(jobs);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_jobUpdateService != null)
        {
            foreach (var job in deletedJobs)
            {
                try
                {
                    await _jobUpdateService.NotifyJobDeleted(job.Id, job.ProjectId);
                }
                catch { }
            }
        }

        return deletedJobs.Count;
    }

    public async Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .Include(j => j.Project)
                .ThenInclude(p => p!.ProviderSelections)
            .Include(j => j.Provider)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return;
        }

        // Only refresh for jobs that haven't started processing yet
        if (job.Status != JobStatus.New)
        {
            return;
        }

        job.ExecutionPlan = null;
        await InitializeExecutionPlanAsync(job, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task InitializeExecutionPlanAsync(Job job, CancellationToken cancellationToken)
    {
        var targets = await BuildExecutionPlanAsync(job, cancellationToken);
        job.ExecutionPlan = targets.Count > 0 ? JsonSerializer.Serialize(targets) : null;
        job.ActiveExecutionIndex = 0;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;

        if (job.ProviderId == Guid.Empty && targets.Count > 0)
        {
            job.ProviderId = targets[0].ProviderId;
        }

        if (string.IsNullOrWhiteSpace(job.ModelUsed) && targets.Count > 0)
        {
            job.ModelUsed = targets[0].ModelId;
        }
    }

    private static string BuildContinuationPrompt(string previousGoalPrompt, string followUpPrompt)
    {
        const int maxPromptLength = 2000;
        const string templatePrefix = "Continue the previous job for this project.\nPrevious goal: ";
        const string templateMiddle = "\n\nFollow-up instructions:\n";

        var trimmedFollowUp = followUpPrompt.Trim();
        var maxFollowUpLength = Math.Max(0, maxPromptLength - templatePrefix.Length - templateMiddle.Length);
        if (trimmedFollowUp.Length > maxFollowUpLength)
        {
            trimmedFollowUp = maxFollowUpLength <= 3
                ? trimmedFollowUp[..maxFollowUpLength]
                : trimmedFollowUp[..(maxFollowUpLength - 3)] + "...";
        }

        var reservedLength = templatePrefix.Length + templateMiddle.Length + trimmedFollowUp.Length;
        var availableForPreviousGoal = Math.Max(0, maxPromptLength - reservedLength);
        var previousGoalSnippet = previousGoalPrompt;

        if (previousGoalSnippet.Length > availableForPreviousGoal)
        {
            previousGoalSnippet = availableForPreviousGoal switch
            {
                <= 0 => string.Empty,
                <= 3 => previousGoalSnippet[..availableForPreviousGoal],
                _ => previousGoalSnippet[..(availableForPreviousGoal - 3)] + "..."
            };
        }

        return $"{templatePrefix}{previousGoalSnippet}{templateMiddle}{trimmedFollowUp}";
    }

    private async Task ResetJobForFollowUp(Job job, DateTime submittedAt, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.New;
        job.CancellationRequested = false;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.Output = null;
        job.ErrorMessage = null;
        job.CurrentActivity = "Queued follow-up instructions...";
        job.LastActivityAt = submittedAt;
        job.WorkerInstanceId = null;
        job.LastHeartbeatAt = null;
        job.ProcessId = null;
        job.CommandUsed = null;
        job.ConsoleOutput = null;
        job.GitDiff = null;
        job.GitCommitBefore = null;
        job.GitCommitHash = null;
        job.PullRequestNumber = null;
        job.PullRequestUrl = null;
        job.PullRequestCreatedAt = null;
        job.MergedAt = null;
        job.SessionSummary = null;
        job.ChangedFilesCount = null;
        job.BuildVerified = null;
        job.BuildOutput = null;
        job.PendingInteractionPrompt = null;
        job.InteractionType = null;
        job.InteractionChoices = null;
        job.InteractionRequestedAt = null;
        job.PlanningOutput = null;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningGeneratedAt = null;
        job.CurrentCycle = 1;
        job.ActiveExecutionIndex = 0;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;
        job.InputTokens = null;
        job.OutputTokens = null;
        job.TotalCostUsd = null;

        var attempts = await _dbContext.JobProviderAttempts
            .Where(a => a.JobId == job.Id)
            .ToListAsync(cancellationToken);
        if (attempts.Count > 0)
        {
            _dbContext.JobProviderAttempts.RemoveRange(attempts);
        }
    }

    private async Task<List<JobExecutionTarget>> BuildExecutionPlanAsync(Job job, CancellationToken cancellationToken)
    {
        var enabledProviders = await _dbContext.Providers
            .Where(p => p.IsEnabled)
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);

        if (enabledProviders.Count == 0)
        {
            return [];
        }

        var providerIds = enabledProviders.Select(p => p.Id).ToList();
        var projectSelections = await _dbContext.ProjectProviders
            .Where(pp => pp.ProjectId == job.ProjectId && pp.IsEnabled && providerIds.Contains(pp.ProviderId))
            .OrderBy(pp => pp.Priority)
            .ToListAsync(cancellationToken);

        var providerOrder = BuildProviderOrder(job.ProviderId, enabledProviders, projectSelections);

        var modelLookup = await _dbContext.ProviderModels
            .Where(m => providerIds.Contains(m.ProviderId) && m.IsAvailable)
            .OrderByDescending(m => m.IsDefault)
            .ThenBy(m => m.DisplayName ?? m.ModelId)
            .ToListAsync(cancellationToken);

        var modelsByProvider = modelLookup
            .GroupBy(m => m.ProviderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var targets = new List<JobExecutionTarget>();
        var order = 0;

        foreach (var provider in providerOrder)
        {
            var selection = projectSelections.FirstOrDefault(pp => pp.ProviderId == provider.Id);
            modelsByProvider.TryGetValue(provider.Id, out var providerModels);
            providerModels ??= [];

            var plannedModels = new List<(string? ModelId, string Source)>();
            if (provider.Id == job.ProviderId)
            {
                if (!string.IsNullOrWhiteSpace(job.ModelUsed))
                {
                    plannedModels.Add((job.ModelUsed, "job-selected-model"));
                }
                else if (!string.IsNullOrWhiteSpace(selection?.PreferredModelId))
                {
                    plannedModels.Add((selection.PreferredModelId, "project-preferred-model"));
                }
            }
            else if (!string.IsNullOrWhiteSpace(selection?.PreferredModelId))
            {
                plannedModels.Add((selection.PreferredModelId, "project-preferred-model"));
            }

            var defaultModel = providerModels.FirstOrDefault(m => m.IsDefault);
            if (defaultModel != null)
            {
                plannedModels.Add((defaultModel.ModelId, "provider-default-model"));
            }

            foreach (var model in providerModels)
            {
                plannedModels.Add((model.ModelId, "provider-available-model"));
            }

            if (plannedModels.Count == 0)
            {
                plannedModels.Add((null, "provider-default-model"));
            }

            foreach (var candidate in plannedModels)
            {
                if (targets.Any(existing => existing.ProviderId == provider.Id && existing.ModelId == candidate.ModelId))
                {
                    continue;
                }

                targets.Add(new JobExecutionTarget
                {
                    ProviderId = provider.Id,
                    ProviderName = provider.Name,
                    ModelId = candidate.ModelId,
                    Order = order++,
                    Source = candidate.Source
                });
            }
        }

        return targets;
    }

    private async Task ValidateRequestedExecutionAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.ProviderId == Guid.Empty)
        {
            if (!string.IsNullOrWhiteSpace(job.ModelUsed))
            {
                throw new InvalidOperationException("Selecting a model requires selecting a provider.");
            }

            return;
        }

        var providerExists = await _dbContext.Providers
            .AnyAsync(provider => provider.Id == job.ProviderId && provider.IsEnabled, cancellationToken);
        if (!providerExists)
        {
            throw new InvalidOperationException("The selected provider is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(job.ModelUsed))
        {
            return;
        }

        var modelExists = await _dbContext.ProviderModels
            .AnyAsync(model =>
                model.ProviderId == job.ProviderId &&
                model.IsAvailable &&
                model.ModelId == job.ModelUsed,
                cancellationToken);
        if (!modelExists)
        {
            throw new InvalidOperationException("The selected model is not available for the chosen provider.");
        }
    }

    private static List<Provider> BuildProviderOrder(Guid selectedProviderId, List<Provider> enabledProviders, List<ProjectProvider> projectSelections)
    {
        if (projectSelections.Count == 0)
        {
            return OrderProvidersWithSelectionFirst(selectedProviderId, enabledProviders);
        }

        var providerById = enabledProviders.ToDictionary(p => p.Id);
        var orderedProviders = projectSelections
            .Where(pp => providerById.ContainsKey(pp.ProviderId))
            .Select(pp => providerById[pp.ProviderId])
            .ToList();

        return OrderProvidersWithSelectionFirst(selectedProviderId, orderedProviders);
    }

    private static List<Provider> OrderProvidersWithSelectionFirst(Guid selectedProviderId, List<Provider> providers)
    {
        if (selectedProviderId == Guid.Empty)
        {
            return providers;
        }

        var selectedProvider = providers.FirstOrDefault(p => p.Id == selectedProviderId);
        if (selectedProvider == null)
        {
            return providers;
        }

        return [selectedProvider, .. providers.Where(p => p.Id != selectedProviderId)];
    }

    private IQueryable<Job> BuildFilteredJobsQuery(Guid? projectId, string statusFilter)
    {
        var query = _dbContext.Jobs.AsQueryable();

        if (projectId.HasValue)
        {
            query = query.Where(job => job.ProjectId == projectId.Value);
        }

        return NormalizeStatusFilter(statusFilter) switch
        {
            "active" => query.Where(job =>
                job.Status == JobStatus.New ||
                job.Status == JobStatus.Started ||
                job.Status == JobStatus.Planning ||
                job.Status == JobStatus.Processing),
            "completed" => query.Where(job => job.Status == JobStatus.Completed),
            "failed" => query.Where(job =>
                job.Status == JobStatus.Failed ||
                job.Status == JobStatus.Cancelled),
            _ => query
        };
    }

    private static string NormalizeStatusFilter(string? statusFilter)
    {
        if (string.IsNullOrWhiteSpace(statusFilter))
        {
            return "all";
        }

        return statusFilter.Trim().ToLowerInvariant() switch
        {
            "active" => "active",
            "completed" => "completed",
            "failed" => "failed",
            _ => "all"
        };
    }

    private static void NormalizeJobForPersistence(Job job)
    {
        job.GoalPrompt = job.GoalPrompt?.Trim() ?? string.Empty;
        job.Title = JobTitleHelper.BuildSafeJobTitle(job.Title, job.GoalPrompt);
        job.ModelUsed = string.IsNullOrWhiteSpace(job.ModelUsed) ? null : job.ModelUsed.Trim();
        job.PlanningOutput = string.IsNullOrWhiteSpace(job.PlanningOutput) ? null : job.PlanningOutput.Trim();
        job.PlanningModelUsed = string.IsNullOrWhiteSpace(job.PlanningModelUsed) ? null : job.PlanningModelUsed.Trim();
        job.Branch = string.IsNullOrWhiteSpace(job.Branch) ? null : job.Branch.Trim();
        job.TargetBranch = string.IsNullOrWhiteSpace(job.TargetBranch) ? null : job.TargetBranch.Trim();
    }

    private static int NormalizePageSize(int pageSize, int defaultPageSize)
    {
        if (pageSize <= 0)
        {
            return defaultPageSize;
        }

        return Math.Min(pageSize, MaxPageSize);
    }

    private static int NormalizePageNumber(int pageNumber, int pageSize, int totalCount)
    {
        if (totalCount <= 0)
        {
            return 1;
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Math.Min(Math.Max(pageNumber, 1), totalPages);
    }
}
