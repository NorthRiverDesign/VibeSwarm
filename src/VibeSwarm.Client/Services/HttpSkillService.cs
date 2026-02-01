using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpSkillService : ISkillService
{
    private readonly HttpClient _http;
    public HttpSkillService(HttpClient http) => _http = http;

    public async Task<IEnumerable<Skill>> GetAllAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Skill>>("/api/skills", ct) ?? [];

    public async Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Skill>>("/api/skills/enabled", ct) ?? [];

    public async Task<Skill?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Skill>($"/api/skills/{id}", ct);

    public async Task<Skill?> GetByNameAsync(string name, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<Skill>($"/api/skills/by-name/{Uri.EscapeDataString(name)}", ct);

    public async Task<Skill> CreateAsync(Skill skill, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/skills", skill, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Skill>(ct) ?? skill;
    }

    public async Task<Skill> UpdateAsync(Skill skill, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/skills/{skill.Id}", skill, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Skill>(ct) ?? skill;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _http.DeleteAsync($"/api/skills/{id}", ct);

    public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default)
    {
        var url = $"/api/skills/name-exists?name={Uri.EscapeDataString(name)}";
        if (excludeId.HasValue) url += $"&excludeId={excludeId}";
        return await _http.GetFromJsonAsync<bool>(url, ct);
    }
}
