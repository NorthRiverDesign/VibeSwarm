using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/ideas")]
[Authorize]
public class IdeasController : ControllerBase
{
    private readonly IIdeaService _ideaService;

    public IdeasController(IIdeaService ideaService) => _ideaService = ideaService;

    [HttpGet("project/{projectId:guid}")]
    public async Task<IActionResult> GetByProject(Guid projectId, CancellationToken ct) => Ok(await _ideaService.GetByProjectIdAsync(projectId, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var idea = await _ideaService.GetByIdAsync(id, ct);
        return idea == null ? NotFound() : Ok(idea);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Idea idea, CancellationToken ct) => Ok(await _ideaService.CreateAsync(idea, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Idea idea, CancellationToken ct)
    {
        idea.Id = id;
        return Ok(await _ideaService.UpdateAsync(idea, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _ideaService.DeleteAsync(id, ct); return Ok(); }

    [HttpGet("project/{projectId:guid}/next-unprocessed")]
    public async Task<IActionResult> GetNextUnprocessed(Guid projectId, CancellationToken ct) => Ok(await _ideaService.GetNextUnprocessedAsync(projectId, ct));

    [HttpPost("{id:guid}/convert-to-job")]
    public async Task<IActionResult> ConvertToJob(Guid id, CancellationToken ct)
    {
        // First check if the idea is already being processed (before acquiring lock)
        var idea = await _ideaService.GetByIdAsync(id, ct);
        if (idea == null)
        {
            return NotFound(new { error = "Idea not found", code = "NOT_FOUND" });
        }
        if (idea.IsProcessing || idea.JobId.HasValue)
        {
            // Return 409 Conflict - idea is already being processed by another user
            return Conflict(new { error = "This idea is already being processed", code = "ALREADY_PROCESSING", ideaId = id, jobId = idea.JobId });
        }

        var job = await _ideaService.ConvertToJobAsync(id, ct);
        if (job == null)
        {
            // The lock prevented double-start, or another error occurred
            return Conflict(new { error = "Could not start this idea. It may have been started by another user.", code = "START_FAILED" });
        }
        return Ok(job);
    }

    [HttpPost("complete-from-job/{jobId:guid}")]
    public async Task<IActionResult> CompleteFromJob(Guid jobId, CancellationToken ct)
        => await _ideaService.CompleteIdeaFromJobAsync(jobId, ct) ? Ok() : BadRequest();

    [HttpGet("by-job/{jobId:guid}")]
    public async Task<IActionResult> GetByJobId(Guid jobId, CancellationToken ct) => Ok(await _ideaService.GetByJobIdAsync(jobId, ct));

    [HttpPost("project/{projectId:guid}/start-processing")]
    public async Task<IActionResult> StartProcessing(Guid projectId, CancellationToken ct) { await _ideaService.StartProcessingAsync(projectId, ct); return Ok(); }

    [HttpPost("project/{projectId:guid}/stop-processing")]
    public async Task<IActionResult> StopProcessing(Guid projectId, CancellationToken ct) { await _ideaService.StopProcessingAsync(projectId, ct); return Ok(); }

    [HttpGet("project/{projectId:guid}/processing-active")]
    public async Task<IActionResult> IsProcessingActive(Guid projectId, CancellationToken ct) => Ok(await _ideaService.IsProcessingActiveAsync(projectId, ct));

    [HttpPut("project/{projectId:guid}/reorder")]
    public async Task<IActionResult> Reorder(Guid projectId, [FromBody] List<Guid> ideaIds, CancellationToken ct) { await _ideaService.ReorderIdeasAsync(projectId, ideaIds, ct); return Ok(); }

    [HttpPost("{id:guid}/copy")]
    public async Task<IActionResult> Copy(Guid id, [FromBody] TransferRequest req, CancellationToken ct) => Ok(await _ideaService.CopyToProjectAsync(id, req.TargetProjectId, ct));

    [HttpPost("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, [FromBody] TransferRequest req, CancellationToken ct) => Ok(await _ideaService.MoveToProjectAsync(id, req.TargetProjectId, ct));

    [HttpPost("{id:guid}/expand")]
    public async Task<IActionResult> Expand(Guid id, CancellationToken ct)
    {
        var result = await _ideaService.ExpandIdeaAsync(id, ct);
        return result == null ? BadRequest() : Ok(result);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveRequest req, CancellationToken ct)
    {
        var result = await _ideaService.ApproveExpansionAsync(id, req.EditedDescription, ct);
        return result == null ? BadRequest() : Ok(result);
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var result = await _ideaService.RejectExpansionAsync(id, ct);
        return result == null ? BadRequest() : Ok(result);
    }

    public record TransferRequest(Guid TargetProjectId);
    public record ApproveRequest(string? EditedDescription);
}
