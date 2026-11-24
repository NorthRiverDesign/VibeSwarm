using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/updates")]
[Authorize]
public class UpdatesController : ControllerBase
{
	private readonly IAppUpdateService _updates;

	public UpdatesController(IAppUpdateService updates) => _updates = updates;

	[HttpGet("status")]
	public async Task<IActionResult> GetStatus(CancellationToken cancellationToken) =>
		Ok(await _updates.GetStatusAsync(cancellationToken));

	[HttpPost("check")]
	public async Task<IActionResult> Check(CancellationToken cancellationToken) =>
		Ok(await _updates.CheckNowAsync(cancellationToken));

	[HttpPost("install")]
	[Authorize(Roles = "Admin")]
	public async Task<IActionResult> Install(CancellationToken cancellationToken) =>
		Ok(await _updates.StartUpdateAsync(cancellationToken));

	[HttpPost("cancel")]
	[Authorize(Roles = "Admin")]
	public async Task<IActionResult> Cancel(CancellationToken cancellationToken) =>
		Ok(await _updates.CancelUpdateAsync(cancellationToken));
}
