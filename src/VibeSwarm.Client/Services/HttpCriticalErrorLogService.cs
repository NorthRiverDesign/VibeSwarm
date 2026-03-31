using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpCriticalErrorLogService : ICriticalErrorLogService
{
	private readonly HttpClient _http;

	public HttpCriticalErrorLogService(HttpClient http)
	{
		_http = http;
	}

	public async Task<CriticalErrorLogEntry> LogAsync(CriticalErrorLogEntry entry, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync("/api/diagnostics/critical-errors", entry, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
		return await response.ReadJsonAsync(entry, cancellationToken);
	}

	public async Task<IReadOnlyList<CriticalErrorLogEntry>> GetRecentAsync(int limit = 25, CancellationToken cancellationToken = default)
	{
		return await _http.GetJsonAsync($"/api/diagnostics/critical-errors?limit={limit}", new List<CriticalErrorLogEntry>(), cancellationToken);
	}

	public async Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync("/api/diagnostics/critical-errors/prune", null, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
	}
}
