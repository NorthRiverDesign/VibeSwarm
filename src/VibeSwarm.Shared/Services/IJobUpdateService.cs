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
}
