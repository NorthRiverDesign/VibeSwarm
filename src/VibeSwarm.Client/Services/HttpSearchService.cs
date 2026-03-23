using VibeSwarm.Shared.Models;

namespace VibeSwarm.Client.Services;

public class HttpSearchService
{
    private readonly HttpClient _http;
    public HttpSearchService(HttpClient http) => _http = http;

    public async Task<GlobalSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return new GlobalSearchResult();

        return await _http.GetJsonAsync(
            $"/api/search?q={Uri.EscapeDataString(query.Trim())}",
            new GlobalSearchResult(),
            ct);
    }
}
