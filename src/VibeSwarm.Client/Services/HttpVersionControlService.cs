using System.Net.Http.Json;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Client.Services;

public class HttpVersionControlService : IVersionControlService
{
    private readonly HttpClient _http;
    public HttpVersionControlService(HttpClient http) => _http = http;

    public async Task<bool> IsGitAvailableAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<bool>("/api/git/available", ct);

    public async Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<bool>($"/api/git/is-repo?path={Enc(workingDirectory)}", ct);

    public async Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<string?>($"/api/git/commit-hash?path={Enc(workingDirectory)}", ct);

    public async Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<string?>($"/api/git/branch?path={Enc(workingDirectory)}", ct);

    public async Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken ct = default)
        => await _http.GetFromJsonAsync<string?>($"/api/git/remote-url?path={Enc(workingDirectory)}&remote={Uri.EscapeDataString(remoteName)}", ct);

    public async Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<bool>($"/api/git/has-changes?path={Enc(workingDirectory)}", ct);

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/changed-files?path={Enc(workingDirectory)}";
        if (baseCommit != null) url += $"&baseCommit={Uri.EscapeDataString(baseCommit)}";
        return await _http.GetFromJsonAsync<List<string>>(url, ct) ?? [];
    }

    public async Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/diff?path={Enc(workingDirectory)}";
        if (baseCommit != null) url += $"&baseCommit={Uri.EscapeDataString(baseCommit)}";
        return await _http.GetFromJsonAsync<string?>(url, ct);
    }

    public async Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/diff-range?path={Enc(workingDirectory)}&from={Uri.EscapeDataString(fromCommit)}";
        if (toCommit != null) url += $"&to={Uri.EscapeDataString(toCommit)}";
        return await _http.GetFromJsonAsync<string?>(url, ct);
    }

    public async Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/diff-summary?path={Enc(workingDirectory)}";
        if (baseCommit != null) url += $"&baseCommit={Uri.EscapeDataString(baseCommit)}";
        return await _http.GetFromJsonAsync<GitDiffSummary?>(url, ct);
    }

    public async Task<GitOperationResult> CommitAllChangesAsync(string workingDirectory, string commitMessage, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/commit", new { Path = workingDirectory, Message = commitMessage }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false, Error = "Failed to parse response" };
    }

    public async Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/push", new { Path = workingDirectory, Remote = remoteName, Branch = branchName }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false, Error = "Failed to parse response" };
    }

    public async Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/commit-and-push", new { Path = workingDirectory, Message = commitMessage, Remote = remoteName }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false, Error = "Failed to parse response" };
    }

    public async Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<GitBranchInfo>>($"/api/git/branches?path={Enc(workingDirectory)}&includeRemote={includeRemote}", ct) ?? [];

    public async Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/fetch", new { Path = workingDirectory, Remote = remoteName, Prune = prune }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false };
    }

    public async Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/checkout", new { Path = workingDirectory, Branch = branchName, Remote = remoteName }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false };
    }

    public async Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/sync", new { Path = workingDirectory, Remote = remoteName }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false };
    }

    public async Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/clone", new { Url = repositoryUrl, Path = targetDirectory, Branch = branch }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false };
    }

    public string GetGitHubCloneUrl(string ownerAndRepo, bool useSsh = true)
        => useSsh ? $"git@github.com:{ownerAndRepo}.git" : $"https://github.com/{ownerAndRepo}.git";

    public string? ExtractGitHubRepository(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        // SSH format: git@github.com:owner/repo.git
        if (remoteUrl.StartsWith("git@github.com:"))
        {
            var path = remoteUrl["git@github.com:".Length..].TrimEnd('/');
            if (path.EndsWith(".git")) path = path[..^4];
            return path;
        }
        // HTTPS format: https://github.com/owner/repo.git
        if (remoteUrl.Contains("github.com/"))
        {
            var idx = remoteUrl.IndexOf("github.com/") + "github.com/".Length;
            var path = remoteUrl[idx..].TrimEnd('/');
            if (path.EndsWith(".git")) path = path[..^4];
            return path;
        }
        return null;
    }

    public async Task<GitOperationResult> CreateBranchAsync(string workingDirectory, string branchName, bool switchToBranch = true, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/create-branch", new { Path = workingDirectory, Branch = branchName, SwitchToBranch = switchToBranch }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false };
    }

    public async Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/discard", new { Path = workingDirectory, IncludeUntracked = includeUntracked }, ct);
        return await response.Content.ReadFromJsonAsync<GitOperationResult>(ct) ?? new GitOperationResult { Success = false };
    }

    public async Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/commit-log?path={Enc(workingDirectory)}&from={Uri.EscapeDataString(fromCommit)}";
        if (toCommit != null) url += $"&to={Uri.EscapeDataString(toCommit)}";
        return await _http.GetFromJsonAsync<List<string>>(url, ct) ?? [];
    }

    private static string Enc(string value) => Uri.EscapeDataString(value);
}
