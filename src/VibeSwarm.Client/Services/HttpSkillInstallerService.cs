using System.Net.Http.Json;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Blazor-side client for the install / preview / marketplace endpoints exposed by
/// <c>SkillsController</c>. Implements the same interface the server uses so calling code
/// (the Skills page, modals) can stay provider-neutral.
/// </summary>
public class HttpSkillInstallerService : ISkillInstallerService
{
	private readonly HttpClient _http;

	public HttpSkillInstallerService(HttpClient http) => _http = http;

	public async Task<SkillInstallPreview> PreviewAsync(SkillInstallRequest request, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync("/api/skills/install/preview", request, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
		var result = await response.Content.ReadFromJsonAsync<SkillInstallPreview>(cancellationToken: cancellationToken);
		return result ?? throw new InvalidOperationException("Server returned no preview payload.");
	}

	public async Task<SkillInstallResult> InstallAsync(SkillInstallRequest request, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsJsonAsync("/api/skills/install", request, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
		var result = await response.Content.ReadFromJsonAsync<SkillInstallResult>(cancellationToken: cancellationToken);
		return result ?? throw new InvalidOperationException("Server returned no install result.");
	}

	/// <summary>
	/// Fetches the marketplace catalog. Not part of <see cref="ISkillInstallerService"/>
	/// because listing isn't an install operation, but kept colocated so the UI only has to
	/// depend on one client type.
	/// </summary>
	public async Task<IReadOnlyList<MarketplaceSkillSummary>> GetMarketplaceCatalogAsync(bool refresh = false, CancellationToken cancellationToken = default)
	{
		var url = refresh ? "/api/skills/marketplace?refresh=true" : "/api/skills/marketplace";
		var response = await _http.GetAsync(url, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
		var result = await response.Content.ReadFromJsonAsync<List<MarketplaceSkillSummary>>(cancellationToken: cancellationToken);
		return result ?? [];
	}
}
