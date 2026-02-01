using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
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
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Project>(ct) ?? project;
    }

    public async Task<Project> UpdateAsync(Project project, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/projects/{project.Id}", project, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Project>(ct) ?? project;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _http.DeleteAsync($"/api/projects/{id}", ct);

    public async Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<ProjectWithStats>>("/api/projects/with-stats", ct) ?? [];

    public async Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<DashboardProjectInfo>>("/api/projects/recent-dashboard?count=" + count, ct) ?? [];
}
