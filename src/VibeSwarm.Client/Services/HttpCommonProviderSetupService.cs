using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpCommonProviderSetupService(HttpClient http) : ICommonProviderSetupService
{
	private readonly HttpClient _http = http;

	public async Task<IReadOnlyList<CommonProviderSetupStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
		=> await _http.GetFromJsonAsync<List<CommonProviderSetupStatus>>("/api/providers/common-setup", cancellationToken) ?? [];

	public async Task<IReadOnlyList<CommonProviderSetupStatus>> RefreshAsync(CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync("/api/providers/common-setup/refresh", null, cancellationToken);
		return await response.Content.ReadFromJsonAsync<List<CommonProviderSetupStatus>>(cancellationToken) ?? [];
	}

	public async Task<CommonProviderActionResult> InstallAsync(ProviderType providerType, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync($"/api/providers/common-setup/{providerType}/install", null, cancellationToken);
		return await response.Content.ReadFromJsonAsync<CommonProviderActionResult>(cancellationToken) ?? new CommonProviderActionResult
		{
			Success = false,
			ErrorMessage = response.IsSuccessStatusCode ? "Installation failed." : $"Installation failed with HTTP {(int)response.StatusCode}."
		};
	}

	public async Task<CommonProviderActionResult> SaveAuthenticationAsync(CommonProviderSetupRequest request, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync($"/api/providers/common-setup/{request.ProviderType}/authenticate", request, cancellationToken);
		return await response.Content.ReadFromJsonAsync<CommonProviderActionResult>(cancellationToken) ?? new CommonProviderActionResult
		{
			Success = false,
			ErrorMessage = response.IsSuccessStatusCode ? "Authentication save failed." : $"Authentication save failed with HTTP {(int)response.StatusCode}."
		};
	}
}
