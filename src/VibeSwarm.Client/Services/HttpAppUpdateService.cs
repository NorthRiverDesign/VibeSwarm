using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpAppUpdateService : IAppUpdateService
{
	private readonly HttpClient _http;

	public HttpAppUpdateService(HttpClient http)
	{
		_http = http;
	}

	public Task<AppUpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
		_http.GetJsonAsync("/api/updates/status", new AppUpdateStatus(), cancellationToken);

	public Task<AppUpdateStatus> CheckNowAsync(CancellationToken cancellationToken = default) =>
		PostAsync("/api/updates/check", cancellationToken);

	public Task<AppUpdateStatus> StartUpdateAsync(CancellationToken cancellationToken = default) =>
		PostAsync("/api/updates/install", cancellationToken);

	public Task<AppUpdateStatus> CancelUpdateAsync(CancellationToken cancellationToken = default) =>
		PostAsync("/api/updates/cancel", cancellationToken);

	private async Task<AppUpdateStatus> PostAsync(string uri, CancellationToken cancellationToken)
	{
		var response = await _http.PostAsync(uri, content: null, cancellationToken);
		response.EnsureSuccessStatusCode();
		return await response.ReadJsonAsync(new AppUpdateStatus(), cancellationToken);
	}
}
