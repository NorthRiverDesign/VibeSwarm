using System.Net.Http.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpFolderManagerService : IFolderManagerService
{
	private readonly HttpClient _http;

	public HttpFolderManagerService(HttpClient http) => _http = http;

	public async Task<IReadOnlyList<FolderManagerReport>> GetReportsAsync(Guid folderId, CancellationToken ct = default)
		=> await _http.GetJsonAsync($"/api/projects/folders/{folderId}/manager/reports", new List<FolderManagerReport>(), ct);

	public async Task<FolderManagerRunResult> RunAsync(Guid folderId, FolderManagerRunRequest request, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync($"/api/projects/folders/{folderId}/manager/runs", request, ct);
		if (!response.IsSuccessStatusCode)
		{
			return new FolderManagerRunResult { Error = $"Could not run the manager ({(int)response.StatusCode} {response.ReasonPhrase})." };
		}

		return await response.ReadJsonAsync(new FolderManagerRunResult { Error = "The server sent an empty reply." }, ct);
	}

	public async Task DeleteReportAsync(Guid reportId, CancellationToken ct = default)
	{
		var response = await _http.DeleteAsync($"/api/projects/folders/manager/reports/{reportId}", ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct, "Report not found.");
	}
}
