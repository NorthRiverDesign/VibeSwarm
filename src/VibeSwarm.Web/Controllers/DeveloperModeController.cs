using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/developer-mode")]
[Authorize]
public class DeveloperModeController : ControllerBase
{
	private readonly IDeveloperModeService _developerModeService;

	public DeveloperModeController(IDeveloperModeService developerModeService)
	{
		_developerModeService = developerModeService;
	}

	[HttpGet("status")]
	public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
	{
		return Ok(await _developerModeService.GetStatusAsync(cancellationToken));
	}

	[HttpPost("self-update")]
	[Authorize(Roles = "Admin")]
	public async Task<IActionResult> StartSelfUpdate(CancellationToken cancellationToken)
	{
		return Ok(await _developerModeService.StartSelfUpdateAsync(cancellationToken));
	}
}
