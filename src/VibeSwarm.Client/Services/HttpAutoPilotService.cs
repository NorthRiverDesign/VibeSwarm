using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Client-side HTTP wrapper for the auto-pilot API endpoints.
/// </summary>
public class HttpAutoPilotService : IAutoPilotService
{
	private readonly HttpClient _http;
	public HttpAutoPilotService(HttpClient http) => _http = http;

	private static string Base(Guid projectId) => $"/api/projects/{projectId}/autopilot";

	public async Task<IterationLoop> StartAsync(Guid projectId, AutoPilotConfig config, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync($"{Base(projectId)}/start", config, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
		return await response.ReadJsonAsync<IterationLoop>(null!, ct)
			?? throw new InvalidOperationException("Failed to deserialize auto-pilot loop");
	}

	public async Task StopAsync(Guid projectId, CancellationToken ct = default)
	{
		var response = await _http.PostAsync($"{Base(projectId)}/stop", null, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
	}

	public async Task PauseAsync(Guid projectId, CancellationToken ct = default)
	{
		var response = await _http.PostAsync($"{Base(projectId)}/pause", null, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
	}

	public async Task ResumeAsync(Guid projectId, CancellationToken ct = default)
	{
		var response = await _http.PostAsync($"{Base(projectId)}/resume", null, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
	}

	public async Task<IterationLoop?> GetStatusAsync(Guid projectId, CancellationToken ct = default)
		=> await _http.GetJsonOrNullAsync<IterationLoop>($"{Base(projectId)}/status", ct);

	public async Task<List<IterationLoop>> GetHistoryAsync(Guid projectId, CancellationToken ct = default)
		=> await _http.GetJsonAsync($"{Base(projectId)}/history", new List<IterationLoop>(), ct);

	public async Task<IterationLoop> UpdateConfigAsync(Guid projectId, AutoPilotConfig config, CancellationToken ct = default)
	{
		var response = await _http.PutAsJsonAsync($"{Base(projectId)}/config", config, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
		return await response.ReadJsonAsync<IterationLoop>(null!, ct)
			?? throw new InvalidOperationException("Failed to deserialize auto-pilot loop");
	}
}
