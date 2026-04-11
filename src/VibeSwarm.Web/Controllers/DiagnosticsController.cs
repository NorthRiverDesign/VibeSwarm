using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/diagnostics")]
public class DiagnosticsController : ControllerBase
{
	private readonly ICriticalErrorLogService _criticalErrorLogService;

	public DiagnosticsController(ICriticalErrorLogService criticalErrorLogService)
	{
		_criticalErrorLogService = criticalErrorLogService;
	}

	[HttpGet("critical-errors")]
	[Authorize]
	public async Task<IActionResult> GetCriticalErrors([FromQuery] int limit = 25, CancellationToken cancellationToken = default)
	{
		return Ok(await _criticalErrorLogService.GetRecentAsync(limit, cancellationToken));
	}

	[HttpPost("critical-errors")]
	[AllowAnonymous]
	public async Task<IActionResult> CreateCriticalError([FromBody] CriticalErrorLogEntry entry, CancellationToken cancellationToken = default)
	{
		entry.UserId ??= TryGetUserId(User);
		entry.Url ??= HttpContext.Request.Headers.Referer.ToString();
		entry.UserAgent ??= HttpContext.Request.Headers.UserAgent.ToString();

		return Ok(await _criticalErrorLogService.LogAsync(entry, cancellationToken));
	}

	[HttpPost("critical-errors/prune")]
	[Authorize]
	public async Task<IActionResult> PruneCriticalErrors(CancellationToken cancellationToken = default)
	{
		await _criticalErrorLogService.ApplyRetentionPolicyAsync(cancellationToken);
		return NoContent();
	}

	[HttpGet("ping")]
	[AllowAnonymous]
	public IActionResult Ping()
	{
		return Ok(new
		{
			Ok = true,
			ServerTimeUtc = DateTime.UtcNow
		});
	}

	private static Guid? TryGetUserId(ClaimsPrincipal user)
	{
		var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
		return Guid.TryParse(userId, out var parsedUserId) ? parsedUserId : null;
	}
}
