using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpIdeaService : IIdeaService
{
    private readonly HttpClient _http;
    public HttpIdeaService(HttpClient http) => _http = http;

    public async Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken ct = default)
        => await _http.GetJsonAsync($"/api/ideas/project/{projectId}", new List<Idea>(), ct);

    public async Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = 10, CancellationToken ct = default)
        => await _http.GetJsonAsync($"/api/ideas/project/{projectId}/paged?page={page}&pageSize={pageSize}", new ProjectIdeasListResult(), ct);

    public async Task<Idea?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _http.GetJsonOrNullAsync<Idea>($"/api/ideas/{id}", ct);

    public async Task<Idea> CreateAsync(Idea idea, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/ideas", idea, ct);
        await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
        return await response.ReadJsonAsync(idea, ct);
    }

    public async Task<Idea> UpdateAsync(Idea idea, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/ideas/{idea.Id}", idea, ct);
        await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Idea not found.");
        return await response.ReadJsonAsync(idea, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _http.DeleteAsync($"/api/ideas/{id}", ct);

    public async Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken ct = default)
        => await _http.GetJsonOrNullAsync<Idea>($"/api/ideas/project/{projectId}/next-unprocessed", ct);

    public async Task<Job?> ConvertToJobAsync(Guid ideaId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/ideas/{ideaId}/convert-to-job", null, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.ReadJsonOrNullAsync<Job>(ct);
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
        => await _http.GetJsonOrNullAsync<Idea>($"/api/ideas/by-job/{jobId}", ct);

    public async Task StartProcessingAsync(Guid projectId, bool autoCommit = false, CancellationToken ct = default)
        => await _http.PostAsync($"/api/ideas/project/{projectId}/start-processing?autoCommit={autoCommit}", null, ct);

    public async Task StopProcessingAsync(Guid projectId, CancellationToken ct = default)
        => await _http.PostAsync($"/api/ideas/project/{projectId}/stop-processing", null, ct);

    public async Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken ct = default)
        => await _http.GetJsonValueAsync($"/api/ideas/project/{projectId}/processing-active", false, ct);

    public async Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken ct = default)
        => await _http.GetJsonAsync("/api/ideas/global-processing-status", new GlobalIdeasProcessingStatus(), ct);

    public async Task StartAllProcessingAsync(bool autoCommit = false, CancellationToken ct = default)
        => await _http.PostAsync($"/api/ideas/start-all-processing?autoCommit={autoCommit}", null, ct);

    public async Task StopAllProcessingAsync(CancellationToken ct = default)
        => await _http.PostAsync("/api/ideas/stop-all-processing", null, ct);

    public async Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken ct = default)
        => throw new NotSupportedException("ProcessNextIdeaIfReadyAsync is server-only");

    public async Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("GetActiveProcessingProjectsAsync is server-only");

    public Task RecoverStuckIdeasAsync(CancellationToken ct = default)
        => throw new NotSupportedException("RecoverStuckIdeasAsync is server-only");

    public async Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken ct = default)
        => await _http.PutAsJsonAsync($"/api/ideas/project/{projectId}/reorder", ideaIdsInOrder, ct);

    public async Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/ideas/{ideaId}/copy", new { TargetProjectId = targetProjectId }, ct);
        await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
        return await response.ReadJsonAsync(new Idea(), ct);
    }

    public async Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/ideas/{ideaId}/move", new { TargetProjectId = targetProjectId }, ct);
        await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
        return await response.ReadJsonAsync(new Idea(), ct);
    }

    public async Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/ideas/{ideaId}/expand", request ?? new IdeaExpansionRequest(), ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.ReadJsonOrNullAsync<Idea>(ct);
    }

    public async Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/ideas/{ideaId}/cancel-expansion", null, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.ReadJsonOrNullAsync<Idea>(ct);
    }

    public async Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/ideas/{ideaId}/approve", new { EditedDescription = editedDescription }, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.ReadJsonOrNullAsync<Idea>(ct);
    }

    public async Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/ideas/{ideaId}/reject", null, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.ReadJsonOrNullAsync<Idea>(ct);
    }

    public async Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken ct = default)
    {
        // Suggestion generation can take well over 100 s depending on the selected source.
        // The HttpClient registered in DI uses Timeout.InfiniteTimeSpan, so this
        // CancellationTokenSource is now the sole timeout for the suggestion request.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var response = await _http.PostAsJsonAsync($"/api/ideas/project/{projectId}/suggest", request ?? new SuggestIdeasRequest(), linkedCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
                Console.Error.WriteLine($"[Suggest] HTTP {(int)response.StatusCode}: {body}");
                return new SuggestIdeasResult
                {
                    Stage = SuggestIdeasStage.GenerateFailed,
                    Message = $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}."
                };
            }

            var result = await response.ReadJsonOrNullAsync<SuggestIdeasResult>(CancellationToken.None);
            if (result == null)
            {
                Console.Error.WriteLine("[Suggest] Response body deserialized to null.");
                return new SuggestIdeasResult
                {
                    Stage = SuggestIdeasStage.GenerateFailed,
                    Message = "Server returned an unreadable response."
                };
            }

            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Console.Error.WriteLine("[Suggest] Client-side 5-minute timeout fired.");
            return new SuggestIdeasResult
            {
                Stage = SuggestIdeasStage.GenerateFailed,
                Message = "The request timed out after 5 minutes. Try a smaller or faster model."
            };
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the caller (e.g. user navigated away) — not an error worth surfacing.
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Suggest] Unexpected exception: {ex}");
            return new SuggestIdeasResult
            {
                Stage = SuggestIdeasStage.GenerateFailed,
                Message = $"Request failed: {ex.Message}",
                InferenceError = ex.ToString()
            };
        }
    }
}
