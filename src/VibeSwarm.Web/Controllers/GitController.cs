using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/git")]
[Authorize]
public class GitController : ControllerBase
{
    private readonly IVersionControlService _gitService;

    public GitController(IVersionControlService gitService) => _gitService = gitService;

    [HttpGet("available")]
    public async Task<IActionResult> IsAvailable(CancellationToken ct) => Ok(await _gitService.IsGitAvailableAsync(ct));

    [HttpGet("is-repo")]
    public async Task<IActionResult> IsRepo([FromQuery] string path, CancellationToken ct) => Ok(await _gitService.IsGitRepositoryAsync(path, ct));

    [HttpGet("commit-hash")]
    public async Task<IActionResult> GetCommitHash([FromQuery] string path, CancellationToken ct) => Ok(await _gitService.GetCurrentCommitHashAsync(path, ct));

    [HttpGet("branch")]
    public async Task<IActionResult> GetBranch([FromQuery] string path, CancellationToken ct) => Ok(await _gitService.GetCurrentBranchAsync(path, ct));

    [HttpGet("remote-url")]
    public async Task<IActionResult> GetRemoteUrl([FromQuery] string path, [FromQuery] string remote = "origin", CancellationToken ct = default) => Ok(await _gitService.GetRemoteUrlAsync(path, remote, ct));

    [HttpGet("has-changes")]
    public async Task<IActionResult> HasChanges([FromQuery] string path, CancellationToken ct) => Ok(await _gitService.HasUncommittedChangesAsync(path, ct));

    [HttpGet("changed-files")]
    public async Task<IActionResult> GetChangedFiles([FromQuery] string path, [FromQuery] string? baseCommit = null, CancellationToken ct = default) => Ok(await _gitService.GetChangedFilesAsync(path, baseCommit, ct));

    [HttpGet("diff")]
    public async Task<IActionResult> GetDiff([FromQuery] string path, [FromQuery] string? baseCommit = null, CancellationToken ct = default) => Ok(await _gitService.GetWorkingDirectoryDiffAsync(path, baseCommit, ct));

    [HttpGet("diff-range")]
    public async Task<IActionResult> GetDiffRange([FromQuery] string path, [FromQuery] string from, [FromQuery] string? to = null, CancellationToken ct = default) => Ok(await _gitService.GetCommitRangeDiffAsync(path, from, to, ct));

    [HttpGet("diff-summary")]
    public async Task<IActionResult> GetDiffSummary([FromQuery] string path, [FromQuery] string? baseCommit = null, CancellationToken ct = default) => Ok(await _gitService.GetDiffSummaryAsync(path, baseCommit, ct));

    [HttpGet("branches")]
    public async Task<IActionResult> GetBranches([FromQuery] string path, [FromQuery] bool includeRemote = true, CancellationToken ct = default) => Ok(await _gitService.GetBranchesAsync(path, includeRemote, ct));

    [HttpPost("commit")]
    public async Task<IActionResult> Commit([FromBody] CommitRequest req, CancellationToken ct) => Ok(await _gitService.CommitAllChangesAsync(req.Path, req.Message, ct));

    [HttpPost("push")]
    public async Task<IActionResult> Push([FromBody] PushRequest req, CancellationToken ct) => Ok(await _gitService.PushAsync(req.Path, req.Remote ?? "origin", req.Branch, ct));

    [HttpPost("commit-and-push")]
    public async Task<IActionResult> CommitAndPush([FromBody] CommitAndPushRequest req, CancellationToken ct) => Ok(await _gitService.CommitAndPushAsync(req.Path, req.Message, req.Remote ?? "origin", null, ct));

    [HttpPost("fetch")]
    public async Task<IActionResult> Fetch([FromBody] FetchRequest req, CancellationToken ct) => Ok(await _gitService.FetchAsync(req.Path, req.Remote ?? "origin", req.Prune, ct));

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest req, CancellationToken ct) => Ok(await _gitService.HardCheckoutBranchAsync(req.Path, req.Branch, req.Remote ?? "origin", null, ct));

    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncRequest req, CancellationToken ct) => Ok(await _gitService.SyncWithOriginAsync(req.Path, req.Remote ?? "origin", null, ct));

    [HttpPost("clone")]
    public async Task<IActionResult> Clone([FromBody] CloneRequest req, CancellationToken ct) => Ok(await _gitService.CloneRepositoryAsync(req.Url, req.Path, req.Branch, null, ct));

    [HttpPost("create-branch")]
    public async Task<IActionResult> CreateBranch([FromBody] CreateBranchRequest req, CancellationToken ct) => Ok(await _gitService.CreateBranchAsync(req.Path, req.Branch, req.SwitchToBranch, ct));

    [HttpPost("discard")]
    public async Task<IActionResult> Discard([FromBody] DiscardRequest req, CancellationToken ct) => Ok(await _gitService.DiscardAllChangesAsync(req.Path, req.IncludeUntracked, ct));

    [HttpGet("commit-log")]
    public async Task<IActionResult> GetCommitLog([FromQuery] string path, [FromQuery] string from, [FromQuery] string? to = null, CancellationToken ct = default) => Ok(await _gitService.GetCommitLogAsync(path, from, to, ct));

    // Request DTOs
    public record CommitRequest(string Path, string Message);
    public record PushRequest(string Path, string? Remote, string? Branch);
    public record CommitAndPushRequest(string Path, string Message, string? Remote);
    public record FetchRequest(string Path, string? Remote, bool Prune = true);
    public record CheckoutRequest(string Path, string Branch, string? Remote);
    public record SyncRequest(string Path, string? Remote);
    public record CloneRequest(string Url, string Path, string? Branch);
    public record CreateBranchRequest(string Path, string Branch, bool SwitchToBranch = true);
    public record DiscardRequest(string Path, bool IncludeUntracked = true);
}
