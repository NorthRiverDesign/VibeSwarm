using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpSettingsService : ISettingsService
{
    private readonly HttpClient _http;
    public HttpSettingsService(HttpClient http) => _http = http;

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<AppSettings>("/api/settings", ct) ?? new AppSettings();

    public async Task<AppSettings> UpdateSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/settings", settings, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AppSettings>(ct) ?? settings;
    }

    public async Task<string?> GetDefaultProjectsDirectoryAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        return settings.DefaultProjectsDirectory;
    }
}
