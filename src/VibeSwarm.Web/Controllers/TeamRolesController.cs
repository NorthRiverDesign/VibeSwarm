using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/team-roles")]
[Authorize]
public class TeamRolesController : ControllerBase
{
	private readonly ITeamRoleService _teamRoleService;

	public TeamRolesController(ITeamRoleService teamRoleService)
	{
		_teamRoleService = teamRoleService;
	}

	[HttpGet]
	public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await _teamRoleService.GetAllAsync(ct));

	[HttpGet("enabled")]
	public async Task<IActionResult> GetEnabled(CancellationToken ct) => Ok(await _teamRoleService.GetEnabledAsync(ct));

	[HttpGet("{id:guid}")]
	public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
	{
		var teamRole = await _teamRoleService.GetByIdAsync(id, ct);
		return teamRole == null ? NotFound() : Ok(teamRole);
	}

	[HttpPost]
	public async Task<IActionResult> Create([FromBody] TeamRole teamRole, CancellationToken ct)
	{
		try
		{
			return Ok(await _teamRoleService.CreateAsync(teamRole, ct));
		}
		catch (ValidationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
		catch (InvalidOperationException ex)
		{
			return BadRequest(new { error = ex.Message });
		}
		catch (DbUpdateException ex)
		{
			return BadRequest(new { error = GetPersistenceErrorMessage(ex) });
		}
	}

	[HttpPut("{id:guid}")]
	public async Task<IActionResult> Update(Guid id, [FromBody] TeamRole teamRole, CancellationToken ct)
	{
		try
		{
			teamRole.Id = id;
			return Ok(await _teamRoleService.UpdateAsync(teamRole, ct));
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
		catch (DbUpdateException ex)
		{
			return BadRequest(new { error = GetPersistenceErrorMessage(ex) });
		}
	}

	[HttpDelete("{id:guid}")]
	public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
	{
		await _teamRoleService.DeleteAsync(id, ct);
		return Ok();
	}

	[HttpGet("name-exists")]
	public async Task<IActionResult> NameExists([FromQuery] string name, [FromQuery] Guid? excludeId = null, CancellationToken ct = default)
		=> Ok(await _teamRoleService.NameExistsAsync(name, excludeId, ct));

	private static string GetPersistenceErrorMessage(DbUpdateException exception)
	{
		var message = exception.InnerException?.Message ?? exception.Message;

		if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) &&
			message.Contains("TeamRoles", StringComparison.OrdinalIgnoreCase) &&
			message.Contains("Name", StringComparison.OrdinalIgnoreCase))
		{
			return "A team role with this name already exists.";
		}

		if (message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
			message.Contains("foreign key", StringComparison.OrdinalIgnoreCase))
		{
			if (message.Contains("DefaultProviderId", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("Providers", StringComparison.OrdinalIgnoreCase))
			{
				return "The selected default provider does not exist.";
			}

			if (message.Contains("SkillId", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("TeamRoleSkills", StringComparison.OrdinalIgnoreCase) ||
				message.Contains("Skills", StringComparison.OrdinalIgnoreCase))
			{
				return "One or more selected skills do not exist.";
			}
		}

		return message;
	}
}
