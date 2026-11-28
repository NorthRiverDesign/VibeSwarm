using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/profiles")]
[Authorize]
public class ProfilesController : ControllerBase
{
	private readonly IProfileService _profileService;

	public ProfilesController(IProfileService profileService)
	{
		_profileService = profileService;
	}

	[HttpGet]
	public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await _profileService.GetAllAsync(ct));

	[HttpGet("{id:guid}")]
	public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
	{
		var profile = await _profileService.GetByIdAsync(id, ct);
		return profile == null ? NotFound() : Ok(profile);
	}

	[HttpPost]
	public async Task<IActionResult> Create([FromBody] Profile profile, CancellationToken ct)
	{
		try
		{
			return Ok(await _profileService.CreateAsync(profile, ct));
		}
		catch (Exception ex) when (ex is ValidationException or InvalidOperationException)
		{
			return BadRequest(new { error = ex.Message });
		}
	}

	[HttpPut("{id:guid}")]
	public async Task<IActionResult> Update(Guid id, [FromBody] Profile profile, CancellationToken ct)
	{
		try
		{
			profile.Id = id;
			return Ok(await _profileService.UpdateAsync(profile, ct));
		}
		catch (ValidationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
		catch (InvalidOperationException ex)
		{
			return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
				? NotFound(new { error = ex.Message })
				: BadRequest(new { error = ex.Message });
		}
	}

	[HttpDelete("{id:guid}")]
	public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
	{
		try
		{
			await _profileService.DeleteAsync(id, ct);
			return Ok();
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}
}
