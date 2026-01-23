using Microsoft.AspNetCore.SignalR;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Hubs;

namespace VibeSwarm.Web.Services;

/// <summary>
/// SignalR-based implementation of job update broadcasting
/// </summary>
public class SignalRJobUpdateService : IJobUpdateService
{
    private readonly IHubContext<JobHub> _hubContext;
    private readonly ILogger<SignalRJobUpdateService> _logger;

    public SignalRJobUpdateService(
        IHubContext<JobHub> hubContext,
        ILogger<SignalRJobUpdateService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyJobStatusChanged(Guid jobId, string status)
    {
        try
        {
            // Notify job-specific subscribers
            await _hubContext.Clients
                .Group($"job-{jobId}")
                .SendAsync("JobStatusChanged", jobId.ToString(), status);

            // Also notify global job list subscribers
            await _hubContext.Clients
                .Group("job-list")
                .SendAsync("JobStatusChanged", jobId.ToString(), status);

            // Also send a general list changed notification for any listeners
            await _hubContext.Clients
                .Group("job-list")
                .SendAsync("JobListChanged");

            _logger.LogDebug("Sent JobStatusChanged notification for job {JobId}: {Status}", jobId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobStatusChanged notification for job {JobId}", jobId);
        }
    }

    public async Task NotifyJobActivity(Guid jobId, string activity, DateTime timestamp)
    {
        try
        {
            // Notify job-specific subscribers
            await _hubContext.Clients
                .Group($"job-{jobId}")
                .SendAsync("JobActivityUpdated", jobId.ToString(), activity, timestamp);

            // Also notify global job list subscribers with compact update
            await _hubContext.Clients
                .Group("job-list")
                .SendAsync("JobActivityUpdated", jobId.ToString(), activity, timestamp);

            _logger.LogDebug("Sent JobActivityUpdated notification for job {JobId}: {Activity}", jobId, activity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobActivityUpdated notification for job {JobId}", jobId);
        }
    }

    public async Task NotifyJobMessageAdded(Guid jobId)
    {
        try
        {
            await _hubContext.Clients
                .Group($"job-{jobId}")
                .SendAsync("JobMessageAdded", jobId.ToString());

            _logger.LogDebug("Sent JobMessageAdded notification for job {JobId}", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobMessageAdded notification for job {JobId}", jobId);
        }
    }

    public async Task NotifyJobCompleted(Guid jobId, bool success, string? errorMessage = null)
    {
        try
        {
            // Notify job-specific subscribers
            await _hubContext.Clients
                .Group($"job-{jobId}")
                .SendAsync("JobCompleted", jobId.ToString(), success, errorMessage);

            // Also notify global job list subscribers
            await _hubContext.Clients
                .Group("job-list")
                .SendAsync("JobCompleted", jobId.ToString(), success, errorMessage);

            _logger.LogDebug("Sent JobCompleted notification for job {JobId}: Success={Success}", jobId, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobCompleted notification for job {JobId}", jobId);
        }
    }

    public async Task NotifyJobListChanged()
    {
        try
        {
            await _hubContext.Clients
                .Group("job-list")
                .SendAsync("JobListChanged");

            _logger.LogDebug("Sent JobListChanged notification to all subscribers");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobListChanged notification");
        }
    }

    public async Task NotifyJobCreated(Guid jobId, Guid projectId)
    {
        try
        {
            // Notify global job list subscribers
            await _hubContext.Clients
                .Group("job-list")
                .SendAsync("JobCreated", jobId.ToString(), projectId.ToString());

            // Notify project-specific subscribers
            await _hubContext.Clients
                .Group($"project-{projectId}")
                .SendAsync("JobCreated", jobId.ToString(), projectId.ToString());

            _logger.LogDebug("Sent JobCreated notification for job {JobId} in project {ProjectId}", jobId, projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobCreated notification for job {JobId}", jobId);
        }
    }

    public async Task NotifyJobDeleted(Guid jobId, Guid projectId)
    {
        try
        {
            // Notify global job list subscribers
            await _hubContext.Clients
                .Group("job-list")
                .SendAsync("JobDeleted", jobId.ToString(), projectId.ToString());

            // Notify project-specific subscribers
            await _hubContext.Clients
                .Group($"project-{projectId}")
                .SendAsync("JobDeleted", jobId.ToString(), projectId.ToString());

            _logger.LogDebug("Sent JobDeleted notification for job {JobId} in project {ProjectId}", jobId, projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobDeleted notification for job {JobId}", jobId);
        }
    }

    public async Task NotifyJobHeartbeat(Guid jobId, DateTime timestamp)
    {
        try
        {
            // Notify job-specific subscribers
            await _hubContext.Clients
                .Group($"job-{jobId}")
                .SendAsync("JobHeartbeat", jobId.ToString(), timestamp);

            _logger.LogDebug("Sent JobHeartbeat notification for job {JobId}", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobHeartbeat notification for job {JobId}", jobId);
        }
    }
}
