using System.Net.Http.Json;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpDatabaseService : IDatabaseService
{
	private readonly HttpClient _http;
	public HttpDatabaseService(HttpClient http) => _http = http;

	public async Task<DatabaseExportDto> ExportAsync(CancellationToken ct = default)
		=> await _http.GetJsonAsync("/api/database/export", new DatabaseExportDto(), ct);

	public async Task<DatabaseImportResult> ImportAsync(DatabaseExportDto export, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/database/import", export, ct);
		await HttpResponseErrorHelper.EnsureSuccessAsync(response, ct);
		return await response.ReadJsonAsync(new DatabaseImportResult(), ct);
	}
}
