using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace VibeSwarm.Web.Hubs;

[Authorize]
public class JobHub : Hub
{
    /// <summary>
    /// Subscribe to updates for a specific job
    /// </summary>
    public async Task SubscribeToJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"job-{jobId}");
    }

    /// <summary>
    /// Unsubscribe from updates for a specific job
    /// </summary>
    public async Task UnsubscribeFromJob(string jobId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job-{jobId}");
    }

    /// <summary>
    /// Subscribe to global job list updates (for job list pages)
    /// </summary>
    public async Task SubscribeToJobList()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "job-list");
    }

    /// <summary>
    /// Unsubscribe from global job list updates
    /// </summary>
    public async Task UnsubscribeFromJobList()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "job-list");
    }

    /// <summary>
    /// Subscribe to updates for a specific project's jobs
    /// </summary>
    public async Task SubscribeToProject(string projectId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project-{projectId}");
    }

    /// <summary>
    /// Unsubscribe from updates for a specific project's jobs
    /// </summary>
    public async Task UnsubscribeFromProject(string projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project-{projectId}");
    }
}
