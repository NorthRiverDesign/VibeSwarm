using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpTeamRoleService : ITeamRoleService
{
	private readonly HttpClient _http;

	public HttpTeamRoleService(HttpClient http)
	{
		_http = http;
	}

	public async Task<IEnumerable<TeamRole>> GetAllAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/team-roles", new List<TeamRole>(), ct);

	public async Task<IEnumerable<TeamRole>> GetEnabledAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/team-roles/enabled", new List<TeamRole>(), ct);

	public async Task<TeamRole?> GetByIdAsync(Guid id, CancellationToken ct = default)
		=> await _http.GetJsonOrNullAsync<TeamRole>($"/api/team-roles/{id}", ct);

	public async Task<TeamRole> CreateAsync(TeamRole teamRole, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/team-roles", CreateRequestPayload(teamRole), ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Team role not found.");
		return await response.ReadJsonAsync(teamRole, ct);
	}

	public async Task<TeamRole> UpdateAsync(TeamRole teamRole, CancellationToken ct = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/team-roles/{teamRole.Id}", CreateRequestPayload(teamRole), ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Team role not found.");
		return await response.ReadJsonAsync(teamRole, ct);
	}

	public async Task DeleteAsync(Guid id, CancellationToken ct = default)
		=> await _http.DeleteAsync($"/api/team-roles/{id}", ct);

	public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default)
	{
		var url = $"/api/team-roles/name-exists?name={Uri.EscapeDataString(name)}";
		if (excludeId.HasValue)
		{
			url += $"&excludeId={excludeId}";
		}

		return await _http.GetJsonValueAsync(url, false, ct);
	}

	private static TeamRole CreateRequestPayload(TeamRole teamRole)
	{
		return new TeamRole
		{
			Id = teamRole.Id,
			Name = teamRole.Name,
			Description = teamRole.Description,
			Responsibilities = teamRole.Responsibilities,
			DefaultProviderId = teamRole.DefaultProviderId,
			DefaultModelId = teamRole.DefaultModelId,
			DefaultReasoningEffort = teamRole.DefaultReasoningEffort,
			IsEnabled = teamRole.IsEnabled,
			SkillLinks = (teamRole.SkillLinks ?? [])
				.GroupBy(link => link.SkillId)
				.Select(group => new TeamRoleSkill
				{
					TeamRoleId = teamRole.Id,
					SkillId = group.Key
				})
				.ToList()
		};
	}
}
