using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IJobService
{
    Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default);
    Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default);
    Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken cancellationToken = default);
    Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default);
    Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default);
    Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateProgressAsync(Guid id, string? currentActivity, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
