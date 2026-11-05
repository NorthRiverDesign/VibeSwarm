using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpLeadService : ILeadService
{
	private readonly HttpClient _http;

	public HttpLeadService(HttpClient http) => _http = http;

	public async Task<LeadStatus> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
		=> await _http.GetJsonAsync($"/api/lead/projects/{projectId}", new LeadStatus(), cancellationToken);

	public async Task<IReadOnlyList<LeadReview>> GetReviewsAsync(Guid projectId, CancellationToken cancellationToken = default)
		=> await _http.GetJsonAsync($"/api/lead/projects/{projectId}/reviews", new List<LeadReview>(), cancellationToken);

	public async Task<LeadReview> StartReviewAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync($"/api/lead/projects/{projectId}/reviews", null, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
		return await response.ReadJsonAsync(new LeadReview { ProjectId = projectId }, cancellationToken);
	}

	public async Task CancelReviewAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync($"/api/lead/projects/{projectId}/reviews/cancel", null, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
	}

	public async Task<Idea?> ApproveSuggestionAsync(Guid ideaId, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync($"/api/lead/suggestions/{ideaId}/approve", null, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken, "That suggestion is gone.");
		return await response.ReadJsonOrNullAsync<Idea>(cancellationToken);
	}

	public async Task DismissSuggestionAsync(Guid ideaId, CancellationToken cancellationToken = default)
	{
		var response = await _http.PostAsync($"/api/lead/suggestions/{ideaId}/dismiss", null, cancellationToken);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, cancellationToken);
	}
}
