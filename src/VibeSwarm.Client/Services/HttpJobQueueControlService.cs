using System.Net.Http.Json;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpJobQueueControlService : IJobQueueControlService
{
	private readonly HttpClient _http;

	public HttpJobQueueControlService(HttpClient http) => _http = http;

	public async Task<JobQueueState> GetStateAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/queue", new JobQueueState(), ct);

	public async Task<JobQueueState> PauseAsync(
		string? reason = null,
		bool cancelRunningJobs = false,
		CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync(
			"/api/queue/pause",
			new { reason, cancelRunning = cancelRunningJobs },
			ct);
		response.EnsureSuccessStatusCode();
		return await response.ReadJsonAsync(new JobQueueState { IsPaused = true }, ct);
	}

	public async Task<JobQueueState> ResumeAsync(CancellationToken ct = default)
	{
		var response = await _http.PostAsync("/api/queue/resume", null, ct);
		response.EnsureSuccessStatusCode();
		return await response.ReadJsonAsync(new JobQueueState(), ct);
	}
}
