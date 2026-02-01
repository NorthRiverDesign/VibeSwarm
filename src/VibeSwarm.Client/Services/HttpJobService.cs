using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpJobService : IJobService
{
    private readonly HttpClient _http;
    public HttpJobService(HttpClient http) => _http = http;

    public async Task<IEnumerable<Job>> GetAllAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Job>>("/api/jobs", ct) ?? [];

    public async Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Job>>($"/api/jobs/project/{projectId}", ct) ?? [];

    public async Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Job>>("/api/jobs/pending", ct) ?? [];

    public async Task<IEnumerable<Job>> GetActiveJobsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Job>>("/api/jobs/active", ct) ?? [];

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Job>($"/api/jobs/{id}", ct);

    public async Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Job>($"/api/jobs/{id}/with-messages", ct);

    public async Task<Job> CreateAsync(Job job, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/jobs", job, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Job>(ct) ?? job;
    }

    public async Task<Job> UpdateStatusAsync(Guid id, JobStatus status, string? output = null, string? errorMessage = null, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{id}/status", new { Status = status.ToString(), Output = output, ErrorMessage = errorMessage }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Job>(ct) ?? new Job();
    }

    public async Task<Job> UpdateJobResultAsync(Guid id, JobStatus status, string? sessionId, string? output, string? errorMessage, int? inputTokens, int? outputTokens, decimal? costUsd, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/jobs/{id}/result", new { Status = status.ToString(), SessionId = sessionId, Output = output, ErrorMessage = errorMessage, InputTokens = inputTokens, OutputTokens = outputTokens, CostUsd = costUsd }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Job>(ct) ?? new Job();
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
        => await _http.GetFromJsonAsync<bool>($"/api/jobs/{id}/cancellation-requested", ct);

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

    public async Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType, string? choices = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/jobs/{id}/pause-interaction", new { InteractionPrompt = interactionPrompt, InteractionType = interactionType, Choices = choices }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id, CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<InteractionInfo>($"/api/jobs/{id}/interaction", ct);
        if (result == null) return null;
        return (result.Prompt, result.Type, result.Choices);
    }

    public async Task<bool> ResumeJobAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/jobs/{id}/resume", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Job>>("/api/jobs/paused", ct) ?? [];

    public async Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<string?>($"/api/jobs/last-model?projectId={projectId}&providerId={providerId}", ct);

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

    private class InteractionInfo
    {
        public string? Prompt { get; set; }
        public string? Type { get; set; }
        public string? Choices { get; set; }
    }
}
