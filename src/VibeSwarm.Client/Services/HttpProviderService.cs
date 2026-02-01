using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpProviderService : IProviderService
{
    private readonly HttpClient _http;
    public HttpProviderService(HttpClient http) => _http = http;

    public IProvider? CreateInstance(Provider config)
        => throw new NotSupportedException("CreateInstance is a server-only operation");

    public async Task<IEnumerable<Provider>> GetAllAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Provider>>("/api/providers", ct) ?? [];

    public async Task<Provider?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Provider>($"/api/providers/{id}", ct);

    public async Task<Provider?> GetDefaultAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Provider?>("/api/providers/default", ct);

    public async Task<Provider> CreateAsync(Provider provider, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/providers", provider, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Provider>(ct) ?? provider;
    }

    public async Task<Provider> UpdateAsync(Provider provider, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/providers/{provider.Id}", provider, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Provider>(ct) ?? provider;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _http.DeleteAsync($"/api/providers/{id}", ct);

    public async Task<bool> TestConnectionAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/providers/{id}/test", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<ConnectionTestResult> TestConnectionWithDetailsAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<ConnectionTestResult>($"/api/providers/{id}/test-details", ct) ?? new ConnectionTestResult();

    public async Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken ct = default)
    {
        var endpoint = isEnabled ? $"/api/providers/{id}/enable" : $"/api/providers/{id}/disable";
        await _http.PostAsync(endpoint, null, ct);
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken ct = default)
        => await _http.PostAsync($"/api/providers/{id}/set-default", null, ct);

    public async Task<SessionSummary> GetSessionSummaryAsync(Guid providerId, string? sessionId, string? workingDirectory = null, string? fallbackOutput = null, CancellationToken ct = default)
    {
        // This is a server-only operation but we provide a stub for the interface
        // In practice, the client doesn't need to call this directly
        return new SessionSummary { Success = false, ErrorMessage = "Session summary retrieval is server-only" };
    }

    public async Task<IEnumerable<ProviderModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<ProviderModel>>($"/api/providers/{providerId}/models", ct) ?? [];

    public async Task<IEnumerable<ProviderModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/providers/{providerId}/refresh-models", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ProviderModel>>(ct) ?? [];
    }

    public async Task SetDefaultModelAsync(Guid providerId, Guid modelId, CancellationToken ct = default)
        => await _http.PutAsJsonAsync($"/api/providers/{providerId}/default-model", new { ModelId = modelId }, ct);
}
