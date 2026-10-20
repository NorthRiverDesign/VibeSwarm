using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Web.Controllers;

/// <summary>
/// Push notification sign-up for the device the request comes from. Endpoints travel in request
/// bodies: they are long URLs and identify the device to its push service.
/// </summary>
[ApiController]
[Route("api/push")]
[Authorize]
public class PushNotificationsController : ControllerBase
{
	private readonly PushNotificationService _pushNotificationService;

	public PushNotificationsController(PushNotificationService pushNotificationService)
		=> _pushNotificationService = pushNotificationService;

	[HttpGet("public-key")]
	public async Task<IActionResult> GetPublicKey(CancellationToken ct)
		=> Ok(new { publicKey = await _pushNotificationService.GetPublicKeyAsync(ct) });

	[HttpPost("devices/lookup")]
	public async Task<IActionResult> GetDevice([FromBody] PushEndpointRequest request, CancellationToken ct)
	{
		var device = await _pushNotificationService.GetDeviceAsync(request.Endpoint, ct);
		return device == null ? NotFound() : Ok(device);
	}

	[HttpPost("devices")]
	public async Task<IActionResult> SaveDevice([FromBody] PushSubscriptionRequest request, CancellationToken ct)
		=> Ok(await _pushNotificationService.SaveDeviceAsync(request, TryGetUserId(), ct));

	[HttpPost("devices/delete")]
	public async Task<IActionResult> DeleteDevice([FromBody] PushEndpointRequest request, CancellationToken ct)
	{
		await _pushNotificationService.DeleteDeviceAsync(request.Endpoint, ct);
		return NoContent();
	}

	[HttpPost("devices/test")]
	public async Task<IActionResult> SendTest([FromBody] PushEndpointRequest request, CancellationToken ct)
		=> Ok(await _pushNotificationService.SendTestAsync(request.Endpoint, ct));

	private Guid? TryGetUserId()
		=> Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;
}
