using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Services;

public class HttpFileSystemService : IFileSystemService
{
    private readonly HttpClient _http;
    public HttpFileSystemService(HttpClient http) => _http = http;

    public async Task<DirectoryListResult> ListDirectoryAsync(string? path, bool directoriesOnly = false)
    {
        var url = "/api/filesystem/list";
        var queryParams = new List<string>();
        if (path != null) queryParams.Add($"path={Uri.EscapeDataString(path)}");
        if (directoriesOnly) queryParams.Add("directoriesOnly=true");
        if (queryParams.Any()) url += "?" + string.Join("&", queryParams);
        return await _http.GetJsonAsync(url, new DirectoryListResult());
    }

    public async Task<bool> DirectoryExistsAsync(string path)
        => await _http.GetJsonValueAsync($"/api/filesystem/exists?path={Uri.EscapeDataString(path)}", false);

    public async Task<List<DriveEntry>> GetDrivesAsync()
        => await _http.GetJsonAsync("/api/filesystem/drives", new List<DriveEntry>());
}
