using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/autopilot")]
[Authorize]
public class AutoPilotController : ControllerBase
{
	private readonly IAutoPilotService _autoPilotService;

	public AutoPilotController(IAutoPilotService autoPilotService) => _autoPilotService = autoPilotService;

	[HttpPost("start")]
	public async Task<IActionResult> Start(Guid projectId, [FromBody] AutoPilotConfig config, CancellationToken ct)
	{
		try
		{
			var loop = await _autoPilotService.StartAsync(projectId, config, ct);
			return Ok(loop);
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}

	[HttpPost("stop")]
	public async Task<IActionResult> Stop(Guid projectId, CancellationToken ct)
	{
		try
		{
			await _autoPilotService.StopAsync(projectId, ct);
			return Ok();
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}

	[HttpPost("pause")]
	public async Task<IActionResult> Pause(Guid projectId, CancellationToken ct)
	{
		try
		{
			await _autoPilotService.PauseAsync(projectId, ct);
			return Ok();
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}

	[HttpPost("resume")]
	public async Task<IActionResult> Resume(Guid projectId, CancellationToken ct)
	{
		try
		{
			await _autoPilotService.ResumeAsync(projectId, ct);
			return Ok();
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}

	[HttpGet("status")]
	public async Task<IActionResult> GetStatus(Guid projectId, CancellationToken ct)
	{
		var loop = await _autoPilotService.GetStatusAsync(projectId, ct);
		return Ok(loop);
	}

	[HttpGet("history")]
	public async Task<IActionResult> GetHistory(Guid projectId, CancellationToken ct)
	{
		var history = await _autoPilotService.GetHistoryAsync(projectId, ct);
		return Ok(history);
	}

	[HttpPut("config")]
	public async Task<IActionResult> UpdateConfig(Guid projectId, [FromBody] AutoPilotConfig config, CancellationToken ct)
	{
		try
		{
			var loop = await _autoPilotService.UpdateConfigAsync(projectId, config, ct);
			return Ok(loop);
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}
}
