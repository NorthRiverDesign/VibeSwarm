namespace VibeSwarm.Shared.Services;

using VibeSwarm.Shared.Models;

public interface IJobUpdateService
{
    Task NotifyJobStatusChanged(Guid jobId, string status);

    Task NotifyJobActivity(Guid jobId, string activity, DateTime timestamp);

    Task NotifyJobMessageAdded(Guid jobId);

    Task NotifyJobCompleted(Guid jobId, bool success, string? errorMessage = null);

    /// <summary>
    /// Notifies ALL clients that the job list has changed (for dashboard/job list pages)
    /// </summary>
    Task NotifyJobListChanged();

    Task NotifyJobCreated(Guid jobId, Guid projectId);

    Task NotifyJobDeleted(Guid jobId, Guid projectId);

    Task NotifyJobHeartbeat(Guid jobId, DateTime timestamp);

    /// <param name="line">The output line</param>
    /// <param name="isError">True if this is stderr output</param>
    /// <param name="timestamp">When the output was received</param>
    Task NotifyJobOutput(Guid jobId, string line, bool isError, DateTime timestamp);

    /// <param name="command">The command being executed</param>
    Task NotifyProcessStarted(Guid jobId, int processId, string command);

    /// <param name="exitCode">The process exit code</param>
    /// <param name="duration">How long the process ran</param>
    Task NotifyProcessExited(Guid jobId, int processId, int exitCode, TimeSpan duration);

    /// <param name="hasChanges">Whether changes were detected</param>
    Task NotifyJobGitDiffUpdated(Guid jobId, bool hasChanges);

    /// <param name="prompt">The prompt/question from the CLI agent</param>
    /// <param name="interactionType">The type of interaction (confirmation, input, choice, etc.)</param>
    /// <param name="choices">Available choices if applicable</param>
    /// <param name="defaultResponse">Suggested default response</param>
    Task NotifyJobInteractionRequired(Guid jobId, string prompt, string interactionType,
        List<string>? choices = null, string? defaultResponse = null);

    Task NotifyJobResumed(Guid jobId);

    /// <param name="currentCycle">Current cycle number (1-based)</param>
    /// <param name="maxCycles">Maximum number of cycles</param>
    Task NotifyJobCycleProgress(Guid jobId, int currentCycle, int maxCycles);

    /// <param name="jobId">The created job ID</param>
    Task NotifyIdeaStarted(Guid ideaId, Guid projectId, Guid jobId);

    /// <summary>
    /// Notifies all clients that ideas auto-processing state changed for a project
    /// </summary>
    /// <param name="isActive">Whether auto-processing is now active</param>
    Task NotifyIdeasProcessingStateChanged(Guid projectId, bool isActive);

    Task NotifyIdeaCreated(Guid ideaId, Guid projectId);

    Task NotifyIdeaDeleted(Guid ideaId, Guid projectId);

    Task NotifyIdeaUpdated(Guid ideaId, Guid projectId);

    /// <summary>
    /// Notifies all clients that a provider is approaching or has reached its usage limit
    /// </summary>
    /// <param name="percentUsed">Current percentage of limit used</param>
    /// <param name="message">Human-readable warning message</param>
    /// <param name="isExhausted">True if the limit has been reached</param>
    /// <param name="resetTime">When the limit resets (if known)</param>
    Task NotifyProviderUsageWarning(Guid providerId, string providerName, int percentUsed,
        string message, bool isExhausted, DateTime? resetTime);

    /// <summary>
    /// Notifies all clients that a provider has been rate limited
    /// </summary>
    /// <param name="message">Human-readable description of the rate limit</param>
    /// <param name="resetTime">When the rate limit is expected to lift (if known)</param>
    Task NotifyProviderRateLimited(Guid providerId, string providerName, string message, DateTime? resetTime);

    Task NotifyAutoPilotStateChanged(Guid projectId, Data.IterationLoop loop);

	Task NotifyDeveloperUpdateStatusChanged(DeveloperModeStatus status);

	Task NotifyDeveloperUpdateOutputAdded(DeveloperUpdateOutputLine line);
}
