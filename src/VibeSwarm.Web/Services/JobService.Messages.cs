using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public partial class JobService
{
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
}
