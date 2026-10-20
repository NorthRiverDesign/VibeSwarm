using System.Net;
using System.Net.Http.Json;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpPushNotificationService : IPushNotificationService
{
	private readonly HttpClient _http;

	public HttpPushNotificationService(HttpClient http) => _http = http;

	public async Task<string> GetPublicKeyAsync(CancellationToken ct = default)
	{
		var response = await _http.GetJsonAsync("/api/push/public-key", new PublicKeyResponse(), ct);
		return response.PublicKey;
	}

	public async Task<PushDeviceSettings?> GetDeviceAsync(string endpoint, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/push/devices/lookup", new PushEndpointRequest { Endpoint = endpoint }, ct);
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
		return await response.ReadJsonOrNullAsync<PushDeviceSettings>(ct);
	}

	public async Task<PushDeviceSettings> SaveDeviceAsync(PushSubscriptionRequest request, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/push/devices", request, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
		return await response.ReadJsonAsync(new PushDeviceSettings { Endpoint = request.Endpoint }, ct);
	}

	public async Task DeleteDeviceAsync(string endpoint, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/push/devices/delete", new PushEndpointRequest { Endpoint = endpoint }, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
	}

	public async Task<PushTestResult> SendTestAsync(string endpoint, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/push/devices/test", new PushEndpointRequest { Endpoint = endpoint }, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
		return await response.ReadJsonAsync(new PushTestResult { Error = "No answer from the server." }, ct);
	}

	private sealed class PublicKeyResponse
	{
		public string PublicKey { get; set; } = string.Empty;
	}
}
