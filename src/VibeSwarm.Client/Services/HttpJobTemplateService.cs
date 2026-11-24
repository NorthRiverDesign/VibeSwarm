using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpJobTemplateService : IJobTemplateService
{
	private readonly HttpClient _http;

	public HttpJobTemplateService(HttpClient http) => _http = http;

	public async Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default)
		=> await _http.GetJsonAsync("/api/job-templates", new List<JobTemplate>(), cancellationToken);

	public async Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
		=> await _http.GetJsonOrNullAsync<JobTemplate>($"/api/job-templates/{id}", cancellationToken);

	public async Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync("/api/job-templates", template, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Template not found.");
		return await response.ReadJsonAsync(template, cancellationToken);
	}

	public async Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/job-templates/{template.Id}", template, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Template not found.");
		return await response.ReadJsonAsync(template, cancellationToken);
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var response = await _http.DeleteAsync($"/api/job-templates/{id}", cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Template not found.");
	}

	public async Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync($"/api/job-templates/{id}/use", null, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Template not found.");
		return await response.ReadJsonAsync(new JobTemplate(), cancellationToken);
	}

	public async Task<IReadOnlyList<JobTemplateProjectRun>> GetRunsAsync(Guid templateId, CancellationToken cancellationToken = default)
		=> await _http.GetJsonAsync($"/api/job-templates/{templateId}/runs", new List<JobTemplateProjectRun>(), cancellationToken);

	public async Task<JobTemplateRunResult> RunAsync(Guid templateId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
	{
		var request = new JobTemplateRunRequest { ProjectIds = projectIds.ToList() };
		var response = await _http.PostAsJsonAsync($"/api/job-templates/{templateId}/run", request, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Template not found.");
		return await response.ReadJsonAsync(new JobTemplateRunResult(), cancellationToken);
	}
}
