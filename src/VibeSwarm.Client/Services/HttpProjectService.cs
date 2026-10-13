using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Client.Services;

public class HttpProjectService : IProjectService
{
    private readonly HttpClient _http;
    public HttpProjectService(HttpClient http) => _http = http;

    public async Task<IEnumerable<Project>> GetAllAsync(CancellationToken ct = default)
        => await _http.GetJsonAsync("/api/projects", new List<Project>(), ct);

    public async Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken ct = default)
        => await _http.GetJsonAsync($"/api/projects/recent?count={count}", new List<Project>(), ct);

    public async Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _http.GetJsonOrNullAsync<Project>($"/api/projects/{id}", ct);

    public async Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken ct = default)
        => await _http.GetJsonOrNullAsync<Project>($"/api/projects/{id}/with-jobs", ct);

    public async Task<Project> CreateAsync(Project project, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/projects", project, ct);
        await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Project not found.");
        return await response.ReadJsonAsync(project, ct);
    }

    public async Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/projects/provision", request, ct);
        await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Project not found.");
        return await response.ReadJsonAsync(request.Project, ct);
    }

    public async Task<GitHubRepositoryBrowserResult> BrowseGitHubRepositoriesAsync(CancellationToken ct = default)
        => await _http.GetJsonAsync("/api/projects/github-repositories", new GitHubRepositoryBrowserResult(), ct);

    public async Task<Project> UpdateAsync(Project project, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/projects/{project.Id}", project, ct);
        await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Project not found.");
        return await response.ReadJsonAsync(project, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _http.DeleteAsync($"/api/projects/{id}", ct);

    public async Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken ct = default)
        => await _http.GetJsonAsync("/api/projects/with-stats", new List<ProjectWithStats>(), ct);

    public async Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken ct = default)
        => await _http.GetJsonAsync("/api/projects/recent-dashboard?count=" + count, new List<DashboardProjectInfo>(), ct);

    public async Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken ct = default)
        => await _http.GetJsonAsync($"/api/projects/dashboard-metrics?rangeDays={rangeDays}", new DashboardJobMetrics { RangeDays = rangeDays }, ct);

	public async Task<IEnumerable<DashboardRunningJobInfo>> GetDashboardRunningJobsAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/projects/dashboard-running-jobs", new List<DashboardRunningJobInfo>(), ct);

	public async Task SetLinkedProjectsAsync(Guid projectId, IEnumerable<Guid> linkedProjectIds, CancellationToken ct = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/projects/{projectId}/links", linkedProjectIds.ToList(), ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Project not found.");
	}

	public async Task<IEnumerable<ProjectFolder>> GetFoldersAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/projects/folders", new List<ProjectFolder>(), ct);

	public async Task<ProjectFolder> CreateFolderAsync(ProjectFolder folder, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/projects/folders", folder, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Folder not found.");
		return await response.ReadJsonAsync(folder, ct);
	}

	public async Task<ProjectFolder> UpdateFolderAsync(ProjectFolder folder, CancellationToken ct = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/projects/folders/{folder.Id}", folder, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Folder not found.");
		return await response.ReadJsonAsync(folder, ct);
	}

	public async Task MoveToFolderAsync(Guid projectId, Guid? folderId, CancellationToken ct = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/projects/{projectId}/folder", new ProjectFolderMoveRequest { FolderId = folderId }, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Project not found.");
	}

	public async Task DeleteFolderAsync(Guid id, CancellationToken ct = default)
	{
		var response = await _http.DeleteAsync($"/api/projects/folders/{id}", ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Folder not found.");
	}

}
