using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpProfileService : IProfileService
{
	private readonly HttpClient _http;

	public HttpProfileService(HttpClient http)
	{
		_http = http;
	}

	public async Task<IEnumerable<Profile>> GetAllAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/profiles", new List<Profile>(), ct);

	public async Task<Profile?> GetByIdAsync(Guid id, CancellationToken ct = default)
		=> await _http.GetJsonOrNullAsync<Profile>($"/api/profiles/{id}", ct);

	public async Task<Profile> CreateAsync(Profile profile, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/profiles", CreateRequestPayload(profile), ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Profile not found.");
		return await response.ReadJsonAsync(profile, ct);
	}

	public async Task<Profile> UpdateAsync(Profile profile, CancellationToken ct = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/profiles/{profile.Id}", CreateRequestPayload(profile), ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Profile not found.");
		return await response.ReadJsonAsync(profile, ct);
	}

	public async Task DeleteAsync(Guid id, CancellationToken ct = default)
	{
		var response = await _http.DeleteAsync($"/api/profiles/{id}", ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Profile not found.");
	}

	private static Profile CreateRequestPayload(Profile profile) => new()
	{
		Id = profile.Id,
		Name = profile.Name,
		Description = profile.Description,
		ProviderId = profile.ProviderId,
		InferenceProviderId = profile.InferenceProviderId,
		ModelId = profile.ModelId,
		ReasoningEffort = profile.ReasoningEffort,
	};
}
