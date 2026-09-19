using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public interface IJobService
{
    Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken cancellationToken = default);
    Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default);
    Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default);
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
    Task<bool> UpdateGitDeliveryAsync(
        Guid id,
        string? commitHash = null,
        int? pullRequestNumber = null,
        string? pullRequestUrl = null,
        DateTime? pullRequestCreatedAt = null,
        DateTime? mergedAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses a job to wait for user interaction
    /// </summary>
    /// <param name="interactionPrompt">The prompt/question from the CLI agent</param>
    /// <param name="interactionType">The type of interaction (confirmation, input, choice, etc.)</param>
    /// <param name="choices">Available choices if applicable (JSON array)</param>
    Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType,
        string? choices = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the pending interaction details for a paused job
    /// </summary>
    /// <returns>The pending interaction prompt, or null if not paused</returns>
    Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id,
        CancellationToken cancellationToken = default);

	Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default);

	/// <summary>
	/// Continues a completed job with follow-up instructions while preserving the existing session context.
	/// </summary>
	/// <param name="followUpPrompt">The follow-up instructions to send</param>
	Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken cancellationToken = default);

    Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last used model for a project and provider combination
    /// </summary>
    /// <returns>The model ID if found, otherwise null</returns>
    Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets a job for retry with optional provider and model changes
    /// </summary>
    /// <param name="providerId">New provider ID (null to keep current)</param>
    /// <param name="modelId">Model ID to use (null for default)</param>
    /// <param name="reasoningEffort">Reasoning effort to use (null for default)</param>
    Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, string? reasoningEffort = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the goal prompt for a job that hasn't started yet
    /// </summary>
    /// <param name="newPrompt">The new goal prompt</param>
    Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels all non-terminal jobs for a project (New, Pending, Started, Processing, Paused, Stalled).
    /// Queued jobs are cancelled immediately; running jobs are force-cancelled.
    /// </summary>
    Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<int> RetrySelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default);

    Task<int> CancelSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default);

    Task<int> PrioritizeSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bypasses the state machine and directly sets a non-terminal job to Failed.
    /// Use as a last resort for jobs stuck in active states with no recovery path.
    /// </summary>
    /// <returns>True if successful; false if job is already in a terminal state or not found</returns>
    Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the execution plan for a pending job using current project settings.
    /// This ensures queued jobs pick up provider/model changes made after creation.
    /// No-op if the job has already started processing.
    /// </summary>
    /// <summary>
    /// Hands a failed job to the next provider in its execution plan and queues it again.
    /// Returns false when no usable provider is left, which is when the job really failed.
    /// </summary>
    Task<bool> TryFailOverToNextExecutionTargetAsync(Guid id, string? reason, CancellationToken cancellationToken = default);

    Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all change-set snapshots recorded for a job (one per follow-up iteration),
    /// ordered by FollowUpIndex ascending.
    /// </summary>
    Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default);
}
