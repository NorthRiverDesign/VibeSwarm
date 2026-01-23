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
        return await _dbContext.Jobs
            .Include(j => j.Project)
            .Include(j => j.Provider)
            .Where(j => j.Status == JobStatus.New && !j.CancellationRequested)
            .OrderBy(j => j.CreatedAt)
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

        // If job hasn't started yet, mark it as cancelled immediately
        if (job.Status == JobStatus.New || job.Status == JobStatus.Pending)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.CurrentActivity = null;
            job.LastActivityAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
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
        // Keep SessionId for potential session continuation
        // Keep InputTokens, OutputTokens, TotalCostUsd for historical tracking
        // Keep Messages for audit trail and context

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job != null)
        {
            _dbContext.Jobs.Remove(job);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
