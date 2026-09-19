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

	public record PauseRequest(string? Reason, bool CancelRunning);
}
