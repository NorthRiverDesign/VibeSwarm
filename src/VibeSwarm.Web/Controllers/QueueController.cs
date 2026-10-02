using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/queue")]
[Authorize]
public class QueueController : ControllerBase
{
	private readonly IJobQueueControlService _queueControl;

	public QueueController(IJobQueueControlService queueControl) => _queueControl = queueControl;

	[HttpGet]
	public async Task<IActionResult> GetState(CancellationToken ct) =>
		Ok(await _queueControl.GetStateAsync(ct));

	/// <summary>
	/// Stops the queue starting anything else. <c>cancelRunning</c> also stops what is
	/// already executing — the difference between "let it finish" and "stop now".
	/// </summary>
	[HttpPost("pause")]
	public async Task<IActionResult> Pause([FromBody] PauseRequest? request, CancellationToken ct) =>
		Ok(await _queueControl.PauseAsync(request?.Reason, request?.CancelRunning ?? false, ct));

	[HttpPost("resume")]
	public async Task<IActionResult> Resume(CancellationToken ct) =>
		Ok(await _queueControl.ResumeAsync(ct));

	[HttpGet("projects/{projectId:guid}")]
	public async Task<IActionResult> GetProjectState(Guid projectId, CancellationToken ct) =>
		Ok(await _queueControl.GetProjectStateAsync(projectId, ct));

	/// <summary>
	/// Holds back one project's queued jobs for a few minutes. Call again to renew.
	/// </summary>
	[HttpPost("projects/{projectId:guid}/pause")]
	public async Task<IActionResult> PauseProject(Guid projectId, CancellationToken ct) =>
		Ok(await _queueControl.PauseProjectAsync(projectId, ct));

	[HttpPost("projects/{projectId:guid}/resume")]
	public async Task<IActionResult> ResumeProject(Guid projectId, CancellationToken ct) =>
		Ok(await _queueControl.ResumeProjectAsync(projectId, ct));

	public record PauseRequest(string? Reason, bool CancelRunning);
}
