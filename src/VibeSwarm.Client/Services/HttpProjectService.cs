using System.Net;
using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpProjectService : IProjectService
{
    private readonly HttpClient _http;
    public HttpProjectService(HttpClient http) => _http = http;

    public async Task<IEnumerable<Project>> GetAllAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Project>>("/api/projects", ct) ?? [];

    public async Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Project>>($"/api/projects/recent?count={count}", ct) ?? [];

    public async Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Project>($"/api/projects/{id}", ct);

    public async Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Project>($"/api/projects/{id}/with-jobs", ct);

    public async Task<Project> CreateAsync(Project project, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/projects", project, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<Project>(ct) ?? project;
    }

    public async Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/projects/provision", request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<Project>(ct) ?? request.Project;
    }

    public async Task<Project> UpdateAsync(Project project, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/projects/{project.Id}", project, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<Project>(ct) ?? project;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _http.DeleteAsync($"/api/projects/{id}", ct);

    public async Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<ProjectWithStats>>("/api/projects/with-stats", ct) ?? [];

    public async Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<DashboardProjectInfo>>("/api/projects/recent-dashboard?count=" + count, ct) ?? [];

    public async Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<DashboardJobMetrics>($"/api/projects/dashboard-metrics?rangeDays={rangeDays}", ct) ?? new DashboardJobMetrics { RangeDays = rangeDays };

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? errorMessage = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ProjectErrorResponse>(cancellationToken);
            errorMessage = error?.Error;
        }
        catch
        {
            // Fall back to the default response handling below when the payload isn't JSON.
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new HttpRequestException(errorMessage, null, response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HttpRequestException("Project not found.", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }

    private sealed class ProjectErrorResponse
    {
        public string? Error { get; set; }
    }
}
