using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpIdeaService : IIdeaService
{
    private readonly HttpClient _http;
    public HttpIdeaService(HttpClient http) => _http = http;

    public async Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Idea>>($"/api/ideas/project/{projectId}", ct) ?? [];

    public async Task<Idea?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Idea>($"/api/ideas/{id}", ct);

    public async Task<Idea> CreateAsync(Idea idea, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/ideas", idea, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Idea>(ct) ?? idea;
    }

    public async Task<Idea> UpdateAsync(Idea idea, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/ideas/{idea.Id}", idea, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Idea>(ct) ?? idea;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _http.DeleteAsync($"/api/ideas/{id}", ct);

    public async Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Idea?>($"/api/ideas/project/{projectId}/next-unprocessed", ct);

    public async Task<Job?> ConvertToJobAsync(Guid ideaId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/ideas/{ideaId}/convert-to-job", null, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<Job>(ct);
    }

    public async Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/ideas/complete-from-job/{jobId}", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/ideas/handle-job-completion/{jobId}?success={success}", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Idea?>($"/api/ideas/by-job/{jobId}", ct);

    public async Task StartProcessingAsync(Guid projectId, CancellationToken ct = default)
        => await _http.PostAsync($"/api/ideas/project/{projectId}/start-processing", null, ct);

    public async Task StopProcessingAsync(Guid projectId, CancellationToken ct = default)
        => await _http.PostAsync($"/api/ideas/project/{projectId}/stop-processing", null, ct);

    public async Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<bool>($"/api/ideas/project/{projectId}/processing-active", ct);

    public async Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken ct = default)
        => throw new NotSupportedException("ProcessNextIdeaIfReadyAsync is server-only");

    public async Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("GetActiveProcessingProjectsAsync is server-only");

    public async Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken ct = default)
        => await _http.PutAsJsonAsync($"/api/ideas/project/{projectId}/reorder", ideaIdsInOrder, ct);

    public async Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/ideas/{ideaId}/copy", new { TargetProjectId = targetProjectId }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Idea>(ct) ?? new Idea();
    }

    public async Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/ideas/{ideaId}/move", new { TargetProjectId = targetProjectId }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Idea>(ct) ?? new Idea();
    }

    public async Task<Idea?> ExpandIdeaAsync(Guid ideaId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/ideas/{ideaId}/expand", null, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<Idea>(ct);
    }

    public async Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/ideas/{ideaId}/approve", new { EditedDescription = editedDescription }, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<Idea>(ct);
    }

    public async Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/ideas/{ideaId}/reject", null, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<Idea>(ct);
    }
}
