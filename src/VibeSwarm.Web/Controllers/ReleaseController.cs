using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/release")]
[Authorize]
public class ReleaseController : ControllerBase
{
	private readonly IProjectReleaseService _releaseService;

	public ReleaseController(IProjectReleaseService releaseService) => _releaseService = releaseService;

	[HttpGet]
	public async Task<IActionResult> GetStatus(Guid projectId, CancellationToken ct)
	{
		try
		{
			return Ok(await _releaseService.GetStatusAsync(projectId, ct));
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
	}

	[HttpPost("deploy")]
	public async Task<IActionResult> Deploy(Guid projectId, CancellationToken ct)
	{
		try
		{
			return Ok(await _releaseService.DeployAsync(projectId, ct));
		}
		catch (KeyNotFoundException)
		{
			return NotFound();
		}
	}
}
