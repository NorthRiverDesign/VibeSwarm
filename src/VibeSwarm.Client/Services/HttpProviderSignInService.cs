using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpProviderSignInService(HttpClient http) : IProviderSignInService
{
	private readonly HttpClient _http = http;

	public async Task<ProviderSignInSession> StartAsync(ProviderType providerType, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync(GetUrl(providerType), null, cancellationToken);
		return await ReadSessionAsync(response, providerType, "Couldn't start signing in", cancellationToken);
	}

	public Task<ProviderSignInSession?> GetAsync(ProviderType providerType, CancellationToken cancellationToken = default)
		=> _http.GetJsonOrNullAsync<ProviderSignInSession>(GetUrl(providerType), cancellationToken);

	public async Task<ProviderSignInSession> SubmitCodeAsync(ProviderType providerType, string code, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync($"{GetUrl(providerType)}/code", new ProviderSignInCodeRequest { Code = code }, cancellationToken);
		return await ReadSessionAsync(response, providerType, "Couldn't send the code", cancellationToken);
	}

	public async Task<ProviderSignInSession?> CancelAsync(ProviderType providerType, CancellationToken cancellationToken = default)
	{
		var response = await _http.DeleteAsync(GetUrl(providerType), cancellationToken);
		response.EnsureSuccessStatusCode();
		return await response.ReadJsonOrNullAsync<ProviderSignInSession>(cancellationToken);
	}

	private static string GetUrl(ProviderType providerType) => $"/api/providers/common-setup/{providerType}/sign-in";

	private static async Task<ProviderSignInSession> ReadSessionAsync(
		HttpResponseMessage response,
		ProviderType providerType,
		string failure,
		CancellationToken cancellationToken)
	{
		var fallback = new ProviderSignInSession
		{
			ProviderType = providerType,
			State = ProviderSignInState.Failed,
			ErrorMessage = $"{failure} (HTTP {(int)response.StatusCode}).",
			StartedAt = DateTime.UtcNow,
			FinishedAt = DateTime.UtcNow
		};

		return response.IsSuccessStatusCode
			? await response.ReadJsonAsync(fallback, cancellationToken)
			: fallback;
	}
}
