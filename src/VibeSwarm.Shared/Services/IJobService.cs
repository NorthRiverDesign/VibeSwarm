using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IJobService
{
    Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<Job>> GetActiveJobsAsync(CancellationToken cancellationToken = default);
    Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default);
    Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken cancellationToken = default);
    Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken cancellationToken = default);
    Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken cancellationToken = default);
    Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ForceCancelAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateProgressAsync(Guid id, string? currentActivity, CancellationToken cancellationToken = default);
    Task<bool> ResetJobAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default);
    Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses a job to wait for user interaction
    /// </summary>
    /// <param name="id">The job ID</param>
    /// <param name="interactionPrompt">The prompt/question from the CLI agent</param>
    /// <param name="interactionType">The type of interaction (confirmation, input, choice, etc.)</param>
    /// <param name="choices">Available choices if applicable (JSON array)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType,
        string? choices = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the pending interaction details for a paused job
    /// </summary>
    /// <param name="id">The job ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The pending interaction prompt, or null if not paused</returns>
    Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a paused job after user provides input
    /// </summary>
    /// <param name="id">The job ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all paused jobs waiting for user interaction
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last used model for a project and provider combination
    /// </summary>
    /// <param name="projectId">The project ID</param>
    /// <param name="providerId">The provider ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The model ID if found, otherwise null</returns>
    Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets a job for retry with optional provider and model changes
    /// </summary>
    /// <param name="id">The job ID</param>
    /// <param name="providerId">New provider ID (null to keep current)</param>
    /// <param name="modelId">Model ID to use (null for default)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful</returns>
    Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the goal prompt for a job that hasn't started yet
    /// </summary>
    /// <param name="id">The job ID</param>
    /// <param name="newPrompt">The new goal prompt</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful</returns>
    Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default);
}
