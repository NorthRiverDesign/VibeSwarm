namespace VibeSwarm.Shared.Services;

/// <summary>
/// Service for broadcasting job updates to connected clients
/// </summary>
public interface IJobUpdateService
{
    /// <summary>
    /// Notifies clients that a job's status has changed
    /// </summary>
    Task NotifyJobStatusChanged(Guid jobId, string status);

    /// <summary>
    /// Notifies clients of a job activity update (progress message)
    /// </summary>
    Task NotifyJobActivity(Guid jobId, string activity, DateTime timestamp);

    /// <summary>
    /// Notifies clients that a new message has been added to a job
    /// </summary>
    Task NotifyJobMessageAdded(Guid jobId);

    /// <summary>
    /// Notifies clients that a job has completed with results
    /// </summary>
    Task NotifyJobCompleted(Guid jobId, bool success, string? errorMessage = null);

    /// <summary>
    /// Notifies ALL clients that the job list has changed (for dashboard/job list pages)
    /// </summary>
    Task NotifyJobListChanged();

    /// <summary>
    /// Notifies clients about a new job being created
    /// </summary>
    Task NotifyJobCreated(Guid jobId, Guid projectId);

    /// <summary>
    /// Notifies clients about a job being deleted
    /// </summary>
    Task NotifyJobDeleted(Guid jobId, Guid projectId);

    /// <summary>
    /// Notifies clients about heartbeat/liveness of a running job
    /// </summary>
    Task NotifyJobHeartbeat(Guid jobId, DateTime timestamp);

    /// <summary>
    /// Streams a line of CLI output to clients watching a job
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="line">The output line</param>
    /// <param name="isError">True if this is stderr output</param>
    /// <param name="timestamp">When the output was received</param>
    Task NotifyJobOutput(Guid jobId, string line, bool isError, DateTime timestamp);

    /// <summary>
    /// Notifies clients that a CLI process has started for a job
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="processId">The system process ID</param>
    /// <param name="command">The command being executed</param>
    Task NotifyProcessStarted(Guid jobId, int processId, string command);

    /// <summary>
    /// Notifies clients that a CLI process has exited
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="processId">The system process ID</param>
    /// <param name="exitCode">The process exit code</param>
    /// <param name="duration">How long the process ran</param>
    Task NotifyProcessExited(Guid jobId, int processId, int exitCode, TimeSpan duration);

    /// <summary>
    /// Notifies clients that a job's git diff has been updated
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="hasChanges">Whether changes were detected</param>
    Task NotifyJobGitDiffUpdated(Guid jobId, bool hasChanges);
}
