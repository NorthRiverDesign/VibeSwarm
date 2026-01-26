using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Hubs;

[Authorize]
public class JobHub : Hub
{
    private readonly IJobService _jobService;
    private readonly IInteractionResponseService _interactionResponseService;
    private readonly ILogger<JobHub> _logger;

    public JobHub(IJobService jobService, IInteractionResponseService interactionResponseService, ILogger<JobHub> logger)
    {
        _jobService = jobService;
        _interactionResponseService = interactionResponseService;
        _logger = logger;
    }

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

    /// <summary>
    /// Submit a response to a paused job that is waiting for user interaction
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="response">The user's response</param>
    /// <returns>True if the response was accepted</returns>
    public Task<bool> SubmitInteractionResponse(string jobId, string response)
    {
        if (!Guid.TryParse(jobId, out var jobGuid))
        {
            _logger.LogWarning("Invalid job ID format for interaction response: {JobId}", jobId);
            return Task.FromResult(false);
        }

        _logger.LogInformation("User submitting interaction response for job {JobId}: {Response}",
            jobId, response.Length > 50 ? response[..50] + "..." : response);

        // Use the shared interaction response service
        var result = _interactionResponseService.SubmitResponse(jobGuid, response);

        if (result)
        {
            _logger.LogInformation("Interaction response delivered to job {JobId}", jobId);
        }
        else
        {
            _logger.LogWarning("No pending interaction handler found for job {JobId}", jobId);
        }

        return Task.FromResult(result);
    }
}
