using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

/// <summary>
/// Client-side HTTP wrapper for a project's release (deploy) endpoints.
/// </summary>
public class HttpProjectReleaseService : IProjectReleaseService
{
	private readonly HttpClient _http;
	public HttpProjectReleaseService(HttpClient http) => _http = http;

	private static string Base(Guid projectId) => $"/api/projects/{projectId}/release";

	public async Task<ProjectReleaseStatus> GetStatusAsync(Guid projectId, CancellationToken ct = default)
		=> await _http.GetJsonAsync(Base(projectId), new ProjectReleaseStatus { Error = "Couldn't load the release status." }, ct);

	public async Task<DeployResult> DeployAsync(Guid projectId, CancellationToken ct = default)
	{
		var response = await _http.PostAsync($"{Base(projectId)}/deploy", null, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
		return await response.ReadJsonAsync(new DeployResult { Outcome = DeployOutcome.Failed, Message = "Couldn't read the deploy result." }, ct);
	}
}
