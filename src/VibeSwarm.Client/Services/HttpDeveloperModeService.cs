using System.Net.Http.Json;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpDeveloperModeService : IDeveloperModeService
{
	private readonly HttpClient _http;

	public HttpDeveloperModeService(HttpClient http)
	{
		_http = http;
	}

	public async Task<DeveloperModeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
	{
		return await _http.GetJsonAsync("/api/developer-mode/status", new DeveloperModeStatus(), cancellationToken);
	}

	public async Task<DeveloperModeStatus> StartSelfUpdateAsync(CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync("/api/developer-mode/self-update", content: null, cancellationToken);
		response.EnsureSuccessStatusCode();
		return await response.ReadJsonAsync(new DeveloperModeStatus(), cancellationToken);
	}
}
