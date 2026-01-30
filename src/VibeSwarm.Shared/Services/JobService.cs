using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public class JobService : IJobService
{
    private readonly VibeSwarmDbContext _dbContext;
    private readonly IJobUpdateService? _jobUpdateService;

    public JobService(VibeSwarmDbContext dbContext, IJobUpdateService? jobUpdateService = null)
    {
        _dbContext = dbContext;
        _jobUpdateService = jobUpdateService;
    }

    public async Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
            .Include(j => j.Provider)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Provider)
            .Where(j => j.ProjectId == projectId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default)
    {
        // Get projects that already have a running job (Started, Processing, or Paused)
        // Only one job should run per project at a time
        var projectsWithRunningJobs = await _dbContext.Jobs
            .Where(j => j.Status == JobStatus.Started || j.Status == JobStatus.Processing || j.Status == JobStatus.Paused)
            .Select(j => j.ProjectId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Return pending jobs ordered by priority (desc) then creation time (oldest first)
        // Exclude jobs from projects that already have a running job
        return await _dbContext.Jobs
            .Include(j => j.Project)
            .Include(j => j.Provider)
            .Where(j => j.Status == JobStatus.New && !j.CancellationRequested)
            .Where(j => !projectsWithRunningJobs.Contains(j.ProjectId))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Job>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
            .Include(j => j.Provider)
            .Where(j => j.Status == JobStatus.Started || j.Status == JobStatus.Processing || j.Status == JobStatus.New)
            .OrderByDescending(j => j.StartedAt ?? j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
            .Include(j => j.Provider)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
            .Include(j => j.Provider)
            .Include(j => j.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default)
    {
        job.Id = Guid.NewGuid();
        job.CreatedAt = DateTime.UtcNow;
        job.Status = JobStatus.New;

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
        if (job.Status != JobStatus.Started && job.Status != JobStatus.Processing)
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

        // Only allow resetting jobs that are in terminal states
        if (job.Status != JobStatus.Completed && job.Status != JobStatus.Failed && job.Status != JobStatus.Cancelled)
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
        if (job.Status != JobStatus.Processing && job.Status != JobStatus.Started)
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

        job.Status = JobStatus.Processing;
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

        // Only allow resetting jobs that are in terminal states
        if (job.Status != JobStatus.Completed && job.Status != JobStatus.Failed && job.Status != JobStatus.Cancelled)
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
        job.SessionId = null; // Clear session for fresh start with potentially new provider
        job.ModelUsed = modelId; // Set the requested model (null means use provider default)
        job.RetryCount++; // Increment retry count to track attempts

        // Clear token/cost tracking for fresh run
        job.InputTokens = null;
        job.OutputTokens = null;
        job.TotalCostUsd = null;

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

        job.GoalPrompt = newPrompt;

        // If job has a title that was derived from the original prompt, update it too
        if (!string.IsNullOrEmpty(job.Title) && job.Title.Length <= 200)
        {
            // Update title to reflect new prompt (truncated to first 200 chars)
            job.Title = newPrompt.Length > 200 ? newPrompt[..197] + "..." : newPrompt;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }
}
