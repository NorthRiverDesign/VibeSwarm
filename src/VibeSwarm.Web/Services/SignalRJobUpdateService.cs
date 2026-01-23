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
            await _hubContext.Clients
                .Group($"job-{jobId}")
                .SendAsync("JobStatusChanged", jobId.ToString(), status);

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
            await _hubContext.Clients
                .Group($"job-{jobId}")
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
            await _hubContext.Clients
                .Group($"job-{jobId}")
                .SendAsync("JobCompleted", jobId.ToString(), success, errorMessage);

            _logger.LogDebug("Sent JobCompleted notification for job {JobId}: Success={Success}", jobId, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending JobCompleted notification for job {JobId}", jobId);
        }
    }
}
