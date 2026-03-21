using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpJobScheduleService : IJobScheduleService
{
	private readonly HttpClient _http;

	public HttpJobScheduleService(HttpClient http) => _http = http;

	public async Task<IEnumerable<JobSchedule>> GetAllAsync(CancellationToken cancellationToken = default)
		=> await _http.GetJsonAsync("/api/schedules", new List<JobSchedule>(), cancellationToken);

	public async Task<JobSchedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
		=> await _http.GetJsonOrNullAsync<JobSchedule>($"/api/schedules/{id}", cancellationToken);

	public async Task<JobSchedule> CreateAsync(JobSchedule schedule, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync("/api/schedules", schedule, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Schedule not found.");
		return await response.ReadJsonAsync(schedule, cancellationToken);
	}

	public async Task<JobSchedule> UpdateAsync(JobSchedule schedule, CancellationToken cancellationToken = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/schedules/{schedule.Id}", schedule, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Schedule not found.");
		return await response.ReadJsonAsync(schedule, cancellationToken);
	}

	public async Task<JobSchedule> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/schedules/{id}/enabled", new UpdateEnabledRequest(isEnabled), cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Schedule not found.");
		return await response.ReadJsonAsync(new JobSchedule(), cancellationToken);
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var response = await _http.DeleteAsync($"/api/schedules/{id}", cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "Schedule not found.");
	}

	private sealed record UpdateEnabledRequest(bool IsEnabled);
}
