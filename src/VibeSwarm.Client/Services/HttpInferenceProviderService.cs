using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Client-side HTTP implementation for managing inference providers and models.
/// </summary>
public class HttpInferenceProviderService : IInferenceProviderService
{
	private readonly HttpClient _http;
	private readonly CachedData<List<InferenceProvider>> _allProvidersCache = new(TimeSpan.FromSeconds(60));

	public HttpInferenceProviderService(HttpClient http) => _http = http;

	public async Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default)
		=> await _allProvidersCache.GetOrFetchAsync(
			async () => await _http.GetJsonAsync("/api/inference/providers", new List<InferenceProvider>(), ct));

	public async Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default)
		=> await _http.GetJsonOrNullAsync<InferenceProvider>($"/api/inference/providers/{id}", ct);

	public async Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default)
	{
		var all = await GetAllAsync(ct);
		return all.Where(p => p.IsEnabled);
	}

	public async Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/inference/providers", provider, ct);
		response.EnsureSuccessStatusCode();
		_allProvidersCache.Invalidate();
		return await response.ReadJsonAsync(provider, ct);
	}

	public async Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/inference/providers/{provider.Id}", provider, ct);
		response.EnsureSuccessStatusCode();
		_allProvidersCache.Invalidate();
		return await response.ReadJsonAsync(provider, ct);
	}

	public async Task DeleteAsync(Guid id, CancellationToken ct = default)
	{
		await _http.DeleteAsync($"/api/inference/providers/{id}", ct);
		_allProvidersCache.Invalidate();
	}

	public async Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
		=> await _http.GetJsonAsync($"/api/inference/providers/{providerId}/models", new List<InferenceModel>(), ct);

	public async Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default)
	{
		// Find the model by modelId to get its Guid
		var models = await GetModelsAsync(providerId, ct);
		var model = models.FirstOrDefault(m => m.ModelId == modelId);
		if (model == null) return;

		await _http.PutAsJsonAsync($"/api/inference/models/{model.Id}/task", new { TaskType = taskType }, ct);
	}

	public async Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default)
	{
		// Client-side: not typically called directly, but provided for interface compliance
		throw new NotSupportedException("GetModelForTaskAsync is a server-side operation");
	}

	/// <summary>
	/// Refreshes models from the provider API and syncs to the database.
	/// </summary>
	public async Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default)
	{
		var response = await _http.PostAsync($"/api/inference/providers/{providerId}/models/refresh", null, ct);
		response.EnsureSuccessStatusCode();
		return await response.ReadJsonAsync(new List<InferenceModel>(), ct);
	}
}
