using System.Net.Http.Json;
using System.Text.Json;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Client.Services;

public class HttpVersionControlService : IVersionControlService
{
    private readonly HttpClient _http;
    public HttpVersionControlService(HttpClient http) => _http = http;

    public async Task<bool> IsGitAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/git/available", ct);
            if (!response.IsSuccessStatusCode) return false;
            var content = await response.Content.ReadAsStringAsync(ct);
            return bool.TryParse(content, out var result) && result;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsGitRepositoryAsync(string workingDirectory, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/git/is-repo?path={Enc(workingDirectory)}";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return false;
            var content = await response.Content.ReadAsStringAsync(ct);
            return bool.TryParse(content, out var result) && result;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetCurrentCommitHashAsync(string workingDirectory, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"/api/git/commit-hash?path={Enc(workingDirectory)}", ct);
            if (!response.IsSuccessStatusCode) return null;
            var content = await response.Content.ReadAsStringAsync(ct);
            // Remove surrounding quotes if present (JSON string format)
            return content.Trim().Trim('"');
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"/api/git/branch?path={Enc(workingDirectory)}", ct);
            if (!response.IsSuccessStatusCode) return null;
            var content = await response.Content.ReadAsStringAsync(ct);
            // Remove surrounding quotes if present (JSON string format)
            return content.Trim().Trim('"');
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetRemoteUrlAsync(string workingDirectory, string remoteName = "origin", CancellationToken ct = default)
        => await GetStringResponseAsync($"/api/git/remote-url?path={Enc(workingDirectory)}&remote={Uri.EscapeDataString(remoteName)}", ct);

	public async Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken ct = default)
	{
		try
		{
            var response = await _http.GetAsync($"/api/git/has-changes?path={Enc(workingDirectory)}", ct);
            if (!response.IsSuccessStatusCode) return false;
            var content = await response.Content.ReadAsStringAsync(ct);
            return bool.TryParse(content, out var result) && result;
        }
        catch
        {
            return false;
		}
	}

	public async Task<GitWorkingTreeStatus> GetWorkingTreeStatusAsync(string workingDirectory, CancellationToken ct = default)
	{
		try
		{
			return await _http.GetJsonAsync($"/api/git/working-tree-status?path={Enc(workingDirectory)}", new GitWorkingTreeStatus(), ct);
		}
		catch
		{
			return new GitWorkingTreeStatus();
		}
	}

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDirectory, string? baseCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/changed-files?path={Enc(workingDirectory)}";
        if (baseCommit != null) url += $"&baseCommit={Uri.EscapeDataString(baseCommit)}";
        return await _http.GetJsonAsync(url, new List<string>(), ct);
    }

    public async Task<string?> GetWorkingDirectoryDiffAsync(string workingDirectory, string? baseCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/diff?path={Enc(workingDirectory)}";
        if (baseCommit != null) url += $"&baseCommit={Uri.EscapeDataString(baseCommit)}";
        return await GetStringResponseAsync(url, ct);
    }

    public async Task<string?> GetCommitRangeDiffAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/diff-range?path={Enc(workingDirectory)}&from={Uri.EscapeDataString(fromCommit)}";
        if (toCommit != null) url += $"&to={Uri.EscapeDataString(toCommit)}";
        return await GetStringResponseAsync(url, ct);
    }

    public async Task<GitDiffSummary?> GetDiffSummaryAsync(string workingDirectory, string? baseCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/diff-summary?path={Enc(workingDirectory)}";
        if (baseCommit != null) url += $"&baseCommit={Uri.EscapeDataString(baseCommit)}";
        return await _http.GetJsonOrNullAsync<GitDiffSummary>(url, ct);
    }

    public async Task<GitOperationResult> CommitAllChangesAsync(
        string workingDirectory,
        string commitMessage,
        CancellationToken ct = default,
        GitCommitOptions? commitOptions = null)
    {
        var response = await _http.PostAsJsonAsync("/api/git/commit", new { Path = workingDirectory, Message = commitMessage, CommitOptions = commitOptions }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<GitOperationResult> PushAsync(string workingDirectory, string remoteName = "origin", string? branchName = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/push", new { Path = workingDirectory, Remote = remoteName, Branch = branchName }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<GitOperationResult> CommitAndPushAsync(string workingDirectory, string commitMessage, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/commit-and-push", new { Path = workingDirectory, Message = commitMessage, Remote = remoteName }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<GitOperationResult> CreatePullRequestAsync(
        string workingDirectory,
        string sourceBranch,
        string targetBranch,
        string title,
        string? body = null,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/create-pull-request", new
        {
            Path = workingDirectory,
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch,
            Title = title,
            Body = body
        }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<GitOperationResult> PreviewMergeBranchAsync(
        string workingDirectory,
        string sourceBranch,
        string targetBranch,
        string remoteName = "origin",
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/preview-merge-branch", new
        {
            Path = workingDirectory,
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch,
            Remote = remoteName
        }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<GitOperationResult> MergeBranchAsync(
        string workingDirectory,
        string sourceBranch,
        string targetBranch,
        string remoteName = "origin",
        Action<string>? progressCallback = null,
        CancellationToken ct = default,
        bool pushAfterMerge = true,
        IReadOnlyList<MergeConflictResolution>? conflictResolutions = null)
    {
        var response = await _http.PostAsJsonAsync("/api/git/merge-branch", new
        {
            Path = workingDirectory,
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch,
            Remote = remoteName,
            PushAfterMerge = pushAfterMerge,
            ConflictResolutions = conflictResolutions
        }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string workingDirectory, bool includeRemote = true, CancellationToken ct = default)
        => await _http.GetJsonAsync($"/api/git/branches?path={Enc(workingDirectory)}&includeRemote={includeRemote}", new List<GitBranchInfo>(), ct);

    public async Task<GitOperationResult> FetchAsync(string workingDirectory, string remoteName = "origin", bool prune = true, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/fetch", new { Path = workingDirectory, Remote = remoteName, Prune = prune }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false }, ct);
    }

    public async Task<GitOperationResult> HardCheckoutBranchAsync(string workingDirectory, string branchName, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/checkout", new { Path = workingDirectory, Branch = branchName, Remote = remoteName }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false }, ct);
    }

    public async Task<GitOperationResult> SyncWithOriginAsync(string workingDirectory, string remoteName = "origin", Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/sync", new { Path = workingDirectory, Remote = remoteName }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false }, ct);
    }

    public async Task<GitOperationResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string? branch = null, Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/clone", new { Url = repositoryUrl, Path = targetDirectory, Branch = branch }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false }, ct);
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
        return await response.ReadJsonAsync(new GitOperationResult { Success = false }, ct);
    }

	public async Task<GitOperationResult> DiscardAllChangesAsync(string workingDirectory, bool includeUntracked = true, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/git/discard", new { Path = workingDirectory, IncludeUntracked = includeUntracked }, ct);
		return await response.ReadJsonAsync(new GitOperationResult { Success = false }, ct);
	}

	public async Task<GitOperationResult> PreserveChangesAsync(string workingDirectory, string message, CancellationToken ct = default)
	{
		var response = await _http.PostAsJsonAsync("/api/git/preserve", new { Path = workingDirectory, Message = message }, ct);
		return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
	}

    public async Task<IReadOnlyList<string>> GetCommitLogAsync(string workingDirectory, string fromCommit, string? toCommit = null, CancellationToken ct = default)
    {
        var url = $"/api/git/commit-log?path={Enc(workingDirectory)}&from={Uri.EscapeDataString(fromCommit)}";
        if (toCommit != null) url += $"&to={Uri.EscapeDataString(toCommit)}";
        return await _http.GetJsonAsync(url, new List<string>(), ct);
    }

    public async Task<GitOperationResult> InitializeRepositoryAsync(string workingDirectory, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/init", new { Path = workingDirectory }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<bool> IsGitHubCliAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/git/gh-available", ct);
            if (!response.IsSuccessStatusCode) return false;
            var content = await response.Content.ReadAsStringAsync(ct);
            return bool.TryParse(content, out var result) && result;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsGitHubCliAuthenticatedAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/git/gh-authenticated", ct);
            if (!response.IsSuccessStatusCode) return false;
            var content = await response.Content.ReadAsStringAsync(ct);
            return bool.TryParse(content, out var result) && result;
        }
        catch
        {
            return false;
        }
    }

    public async Task<GitOperationResult> CreateGitHubRepositoryAsync(
        string workingDirectory,
        string repositoryName,
        string? description = null,
        bool isPrivate = false,
        Action<string>? progressCallback = null,
        CancellationToken ct = default,
        string? gitignoreTemplate = null,
        string? licenseTemplate = null,
        bool initializeReadme = false)
    {
        var response = await _http.PostAsJsonAsync("/api/git/create-github-repo", new
        {
            Path = workingDirectory,
            Name = repositoryName,
            Description = description,
            IsPrivate = isPrivate,
            GitignoreTemplate = gitignoreTemplate,
            LicenseTemplate = licenseTemplate,
            InitializeReadme = initializeReadme
        }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<GitOperationResult> AddRemoteAsync(
        string workingDirectory,
        string remoteName,
        string remoteUrl,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/add-remote", new
        {
            Path = workingDirectory,
            RemoteName = remoteName,
            RemoteUrl = remoteUrl
        }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetRemotesAsync(string workingDirectory, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetJsonAsync($"/api/git/remotes?path={Enc(workingDirectory)}", new Dictionary<string, string>(), ct);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public async Task<GitOperationResult> CloneWithGitHubCliAsync(string ownerRepo, string targetDirectory, Action<string>? progressCallback = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/gh-clone", new { OwnerRepo = ownerRepo, Path = targetDirectory }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    public async Task<GitOperationResult> PruneRemoteBranchesAsync(string workingDirectory, string remoteName = "origin", CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/git/prune", new { Path = workingDirectory, Remote = remoteName }, ct);
        return await response.ReadJsonAsync(new GitOperationResult { Success = false, Error = "Failed to parse response" }, ct);
    }

    private async Task<string?> GetStringResponseAsync(string requestUri, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync(requestUri, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            if (string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return JsonSerializer.Deserialize<string?>(content);
                }
                catch (JsonException)
                {
                    // Fall back to the raw body in case the server returned plain text with the wrong content type.
                }
            }

            return content;
        }
        catch
        {
            return null;
        }
    }

    private static string Enc(string value) => Uri.EscapeDataString(value);
}
