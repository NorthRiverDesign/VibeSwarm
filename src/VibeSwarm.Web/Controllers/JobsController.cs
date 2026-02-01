using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/jobs")]
[Authorize]
public class JobsController : ControllerBase
{
    private readonly IJobService _jobService;

    public JobsController(IJobService jobService) => _jobService = jobService;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => Ok(await _jobService.GetAllAsync(ct));

    [HttpGet("project/{projectId:guid}")]
    public async Task<IActionResult> GetByProject(Guid projectId, CancellationToken ct)
        => Ok(await _jobService.GetByProjectIdAsync(projectId, ct));

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(CancellationToken ct)
        => Ok(await _jobService.GetPendingJobsAsync(ct));

    [HttpGet("active")]
    public async Task<IActionResult> GetActive(CancellationToken ct)
        => Ok(await _jobService.GetActiveJobsAsync(ct));

    [HttpGet("paused")]
    public async Task<IActionResult> GetPaused(CancellationToken ct)
        => Ok(await _jobService.GetPausedJobsAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var job = await _jobService.GetByIdAsync(id, ct);
        return job == null ? NotFound() : Ok(job);
    }

    [HttpGet("{id:guid}/with-messages")]
    public async Task<IActionResult> GetByIdWithMessages(Guid id, CancellationToken ct)
    {
        var job = await _jobService.GetByIdWithMessagesAsync(id, ct);
        return job == null ? NotFound() : Ok(job);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Job job, CancellationToken ct)
    {
        var created = await _jobService.CreateAsync(job, ct);
        return Ok(created);
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<JobStatus>(req.Status, out var status)) return BadRequest("Invalid status");
        var job = await _jobService.UpdateStatusAsync(id, status, req.Output, req.ErrorMessage, ct);
        return Ok(job);
    }

    [HttpPut("{id:guid}/result")]
    public async Task<IActionResult> UpdateResult(Guid id, [FromBody] UpdateResultRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<JobStatus>(req.Status, out var status)) return BadRequest("Invalid status");
        var job = await _jobService.UpdateJobResultAsync(id, status, req.SessionId, req.Output, req.ErrorMessage, req.InputTokens, req.OutputTokens, req.CostUsd, ct);
        return Ok(job);
    }

    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> AddMessage(Guid id, [FromBody] JobMessage message, CancellationToken ct)
    {
        await _jobService.AddMessageAsync(id, message, ct);
        return Ok();
    }

    [HttpPost("{id:guid}/messages/batch")]
    public async Task<IActionResult> AddMessages(Guid id, [FromBody] List<JobMessage> messages, CancellationToken ct)
    {
        await _jobService.AddMessagesAsync(id, messages, ct);
        return Ok();
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        => await _jobService.RequestCancellationAsync(id, ct) ? Ok() : BadRequest();

    [HttpPost("{id:guid}/force-cancel")]
    public async Task<IActionResult> ForceCancel(Guid id, CancellationToken ct)
        => await _jobService.ForceCancelAsync(id, ct) ? Ok() : BadRequest();

    [HttpGet("{id:guid}/cancellation-requested")]
    public async Task<IActionResult> IsCancellationRequested(Guid id, CancellationToken ct)
        => Ok(await _jobService.IsCancellationRequestedAsync(id, ct));

    [HttpPut("{id:guid}/progress")]
    public async Task<IActionResult> UpdateProgress(Guid id, [FromBody] UpdateProgressRequest req, CancellationToken ct)
    {
        await _jobService.UpdateProgressAsync(id, req.CurrentActivity, ct);
        return Ok();
    }

    [HttpPost("{id:guid}/reset")]
    public async Task<IActionResult> Reset(Guid id, CancellationToken ct)
        => await _jobService.ResetJobAsync(id, ct) ? Ok() : BadRequest();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _jobService.DeleteAsync(id, ct);
        return Ok();
    }

    [HttpPut("{id:guid}/git-commit")]
    public async Task<IActionResult> UpdateGitCommit(Guid id, [FromBody] UpdateGitCommitRequest req, CancellationToken ct)
        => await _jobService.UpdateGitCommitHashAsync(id, req.CommitHash, ct) ? Ok() : BadRequest();

    [HttpPut("{id:guid}/git-diff")]
    public async Task<IActionResult> UpdateGitDiff(Guid id, [FromBody] UpdateGitDiffRequest req, CancellationToken ct)
        => await _jobService.UpdateGitDiffAsync(id, req.GitDiff, ct) ? Ok() : BadRequest();

    [HttpPost("{id:guid}/pause-interaction")]
    public async Task<IActionResult> PauseForInteraction(Guid id, [FromBody] PauseInteractionRequest req, CancellationToken ct)
        => await _jobService.PauseForInteractionAsync(id, req.InteractionPrompt, req.InteractionType, req.Choices, ct) ? Ok() : BadRequest();

    [HttpGet("{id:guid}/interaction")]
    public async Task<IActionResult> GetInteraction(Guid id, CancellationToken ct)
    {
        var result = await _jobService.GetPendingInteractionAsync(id, ct);
        if (result == null) return NotFound();
        return Ok(new { result.Value.Prompt, result.Value.Type, result.Value.Choices });
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct)
        => await _jobService.ResumeJobAsync(id, ct) ? Ok() : BadRequest();

    [HttpGet("last-model")]
    public async Task<IActionResult> GetLastUsedModel([FromQuery] Guid projectId, [FromQuery] Guid providerId, CancellationToken ct)
    {
        var model = await _jobService.GetLastUsedModelAsync(projectId, providerId, ct);
        return model != null ? Ok(model) : NoContent();
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> RetryWithOptions(Guid id, [FromBody] RetryRequest req, CancellationToken ct)
        => await _jobService.ResetJobWithOptionsAsync(id, req.ProviderId, req.ModelId, ct) ? Ok() : BadRequest();

    [HttpPut("{id:guid}/prompt")]
    public async Task<IActionResult> UpdatePrompt(Guid id, [FromBody] UpdatePromptRequest req, CancellationToken ct)
        => await _jobService.UpdateJobPromptAsync(id, req.Prompt, ct) ? Ok() : BadRequest();

    // Request DTOs
    public record UpdateStatusRequest(string Status, string? Output, string? ErrorMessage);
    public record UpdateResultRequest(string Status, string? SessionId, string? Output, string? ErrorMessage, int? InputTokens, int? OutputTokens, decimal? CostUsd);
    public record UpdateProgressRequest(string? CurrentActivity);
    public record UpdateGitCommitRequest(string CommitHash);
    public record UpdateGitDiffRequest(string? GitDiff);
    public record PauseInteractionRequest(string InteractionPrompt, string InteractionType, string? Choices);
    public record RetryRequest(Guid? ProviderId, string? ModelId);
    public record UpdatePromptRequest(string Prompt);
}
