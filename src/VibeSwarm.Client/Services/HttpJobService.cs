using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpJobService : IJobService
{
    private readonly HttpClient _http;
    public HttpJobService(HttpClient http) => _http = http;

    public async Task<IEnumerable<Job>> GetAllAsync(CancellationToken ct = default)
        => await _http.GetJsonAsync("/api/jobs", new List<Job>(), ct);

    public async Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var projectQuery = projectId.HasValue ? $"&projectId={projectId.Value}" : string.Empty;
        return await _http.GetJsonAsync($"/api/jobs/paged?status={Uri.EscapeDataString(statusFilter)}&page={page}&pageSize={pageSize}{projectQuery}", new JobsListResult(), ct);
    }

    public async Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        => await _http.GetJsonAsync($"/api/jobs/project/{projectId}", new List<Job>(), ct);

    public async Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, string? search = null, string statusFilter = "all", CancellationToken ct = default)
    {
        var url = $"/api/jobs/project/{projectId}/paged?page={page}&pageSize={pageSize}&statusFilter={Uri.EscapeDataString(statusFilter)}";
        if (!string.IsNullOrWhiteSpace(search))
            url += $"&search={Uri.EscapeDataString(search)}";
        return await _http.GetJsonAsync(url, new ProjectJobsListResult(), ct);
    }

    public async Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken ct = default)
        => await _http.GetJsonAsync("/api/jobs/pending", new List<Job>(), ct);

    public async Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken ct = default)
        => await _http.GetJsonAsync("/api/jobs/active", new List<JobSummary>(), ct);

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _http.GetJsonOrNullAsync<Job>($"/api/jobs/{id}", ct);

    public async Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken ct = default)
        => await _http.GetJsonOrNullAsync<Job>($"/api/jobs/{id}/with-messages", ct);

    public async Task<Job> CreateAsync(Job job, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/jobs", job, ct);
        response.EnsureSuccessStatusCode();
        return await response.ReadJsonAsync(job, ct);
    }

    public async Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{id}/status", new { Status = status.ToString(), Output = output, ErrorMessage = errorMessage }, ct);
        response.EnsureSuccessStatusCode();
        return await response.ReadJsonAsync(new Job(), ct);
    }

    public async Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{id}/result", new { Status = status.ToString(), SessionId = sessionId, Output = output, ErrorMessage = errorMessage, InputTokens = inputTokens, OutputTokens = outputTokens, CostUsd = costUsd }, ct);
        response.EnsureSuccessStatusCode();
        return await response.ReadJsonAsync(new Job(), ct);
    }

    public async Task AddMessageAsync(Guid jobId, JobMessage message, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{jobId}/messages", message, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task AddMessagesAsync(Guid jobId, IEnumerable<JobMessage> messages, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{jobId}/messages/batch", messages, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> RequestCancellationAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/jobs/{id}/cancel", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ForceCancelAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/jobs/{id}/force-cancel", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken ct = default)
        => await _http.GetJsonValueAsync($"/api/jobs/{id}/cancellation-requested", false, ct);

    public async Task UpdateProgressAsync(Guid id, string? currentActivity, CancellationToken ct = default)
    {
        await _http.PutAsJsonAsync($"/api/jobs/{id}/progress", new { CurrentActivity = currentActivity }, ct);
    }

    public async Task<bool> ResetJobAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/jobs/{id}/reset", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _http.DeleteAsync($"/api/jobs/{id}", ct);
    }

    public async Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{id}/git-commit", new { CommitHash = commitHash }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{id}/git-diff", new { GitDiff = gitDiff }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateGitDeliveryAsync(
        Guid id,
        string? commitHash = null,
        int? pullRequestNumber = null,
        string? pullRequestUrl = null,
        DateTime? pullRequestCreatedAt = null,
        DateTime? mergedAt = null,
        CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{id}/git-delivery", new
        {
            CommitHash = commitHash,
            PullRequestNumber = pullRequestNumber,
            PullRequestUrl = pullRequestUrl,
            PullRequestCreatedAt = pullRequestCreatedAt,
            MergedAt = mergedAt
        }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType, string? choices = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{id}/pause-interaction", new { InteractionPrompt = interactionPrompt, InteractionType = interactionType, Choices = choices }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id, CancellationToken ct = default)
    {
        var result = await _http.GetJsonOrNullAsync<InteractionInfo>($"/api/jobs/{id}/interaction", ct);
        if (result == null) return null;
        return (result.Prompt, result.Type, result.Choices);
    }

	public async Task<bool> ResumeJobAsync(Guid id, CancellationToken ct = default)
	{
		var response = await _http.PostAsync($"/api/jobs/{id}/resume", null, ct);
		return response.IsSuccessStatusCode;
	}

	public async Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync($"/api/jobs/{id}/continue", new { FollowUpPrompt = followUpPrompt }, ct);
		return response.IsSuccessStatusCode;
	}

	public async Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/jobs/paused", new List<Job>(), ct);

    public async Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/api/jobs/last-model?projectId={projectId}&providerId={providerId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<string?>(ct);
    }

    public async Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{id}/retry", new { ProviderId = providerId, ModelId = modelId }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{id}/prompt", new { Prompt = newPrompt }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ForceFailJobAsync(Guid id, CancellationToken ct = default)
        => (await _http.PostAsync($"/api/jobs/{id}/force-failed", null, ct)).IsSuccessStatusCode;

    public Task RefreshExecutionPlanAsync(Guid id, CancellationToken ct = default)
        => Task.CompletedTask; // Execution plan refresh is server-side only

    public async Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/jobs/project/{projectId}/cancel-all", null, ct);
        if (!response.IsSuccessStatusCode) return 0;
        var result = await response.Content.ReadFromJsonAsync<JobBulkActionResponse>(ct);
        return result?.Cancelled ?? 0;
    }

    public async Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/jobs/project/{projectId}/completed", ct);
        if (!response.IsSuccessStatusCode) return 0;
        var result = await response.Content.ReadFromJsonAsync<JobBulkActionResponse>(ct);
        return result?.Deleted ?? 0;
    }

    private class InteractionInfo
    {
        public string? Prompt { get; set; }
        public string? Type { get; set; }
        public string? Choices { get; set; }
    }

    private class JobBulkActionResponse
    {
        public int Cancelled { get; set; }
        public int Deleted { get; set; }
    }
}
