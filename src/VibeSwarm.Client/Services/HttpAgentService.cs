using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpAgentService : IAgentService
{
	private readonly HttpClient _http;

	public HttpAgentService(HttpClient http)
	{
		_http = http;
	}

	public async Task<IEnumerable<Agent>> GetAllAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/agents", new List<Agent>(), ct);

	public async Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/agents/enabled", new List<Agent>(), ct);

	public async Task<Agent?> GetByIdAsync(Guid id, CancellationToken ct = default)
		=> await _http.GetJsonOrNullAsync<Agent>($"/api/agents/{id}", ct);

	public async Task<Agent> CreateAsync(Agent agent, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/agents", CreateRequestPayload(agent), ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Team role not found.");
		return await response.ReadJsonAsync(agent, ct);
	}

	public async Task<Agent> UpdateAsync(Agent agent, CancellationToken ct = default)
	{
		var response = await _http.PutAsJsonAsync($"/api/agents/{agent.Id}", CreateRequestPayload(agent), ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Team role not found.");
		return await response.ReadJsonAsync(agent, ct);
	}

	public async Task DeleteAsync(Guid id, CancellationToken ct = default)
		=> await _http.DeleteAsync($"/api/agents/{id}", ct);

	public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default)
	{
		var url = $"/api/agents/name-exists?name={Uri.EscapeDataString(name)}";
		if (excludeId.HasValue)
		{
			url += $"&excludeId={excludeId}";
		}

		return await _http.GetJsonValueAsync(url, false, ct);
	}

	private static Agent CreateRequestPayload(Agent agent)
	{
		return new Agent
		{
			Id = agent.Id,
			Name = agent.Name,
			Description = agent.Description,
			Responsibilities = agent.Responsibilities,
			DefaultProviderId = agent.DefaultProviderId,
			DefaultModelId = agent.DefaultModelId,
			DefaultReasoningEffort = agent.DefaultReasoningEffort,
			DefaultCycleMode = agent.DefaultCycleMode,
			DefaultCycleSessionMode = agent.DefaultCycleSessionMode,
			DefaultMaxCycles = agent.DefaultMaxCycles,
			DefaultCycleReviewPrompt = agent.DefaultCycleReviewPrompt,
			IsEnabled = agent.IsEnabled,
			SkillLinks = (agent.SkillLinks ?? [])
				.GroupBy(link => link.SkillId)
				.Select(group => new AgentSkill
				{
					AgentId = agent.Id,
					SkillId = group.Key
				})
				.ToList()
		};
	}
}
