using System.ComponentModel.DataAnnotations;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/skills")]
[Authorize]
public class SkillsController : ControllerBase
{
    private readonly ISkillService _skillService;

    public SkillsController(ISkillService skillService) => _skillService = skillService;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await _skillService.GetAllAsync(ct));

    [HttpGet("enabled")]
    public async Task<IActionResult> GetEnabled(CancellationToken ct) => Ok(await _skillService.GetEnabledAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var skill = await _skillService.GetByIdAsync(id, ct);
        return skill == null ? NotFound() : Ok(skill);
    }

    [HttpGet("by-name/{name}")]
    public async Task<IActionResult> GetByName(string name, CancellationToken ct)
    {
        var skill = await _skillService.GetByNameAsync(name, ct);
        return skill == null ? NotFound() : Ok(skill);
    }

	[HttpPost]
	public async Task<IActionResult> Create([FromBody] Skill skill, CancellationToken ct)
		=> await ExecuteMutationAsync(() => _skillService.CreateAsync(skill, ct));

	[HttpPut("{id:guid}")]
	public async Task<IActionResult> Update(Guid id, [FromBody] Skill skill, CancellationToken ct)
	{
		skill.Id = id;
		return await ExecuteMutationAsync(() => _skillService.UpdateAsync(skill, ct));
	}

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _skillService.DeleteAsync(id, ct); return Ok(); }

	[HttpGet("name-exists")]
	public async Task<IActionResult> NameExists([FromQuery] string name, [FromQuery] Guid? excludeId = null, CancellationToken ct = default)
		=> Ok(await _skillService.NameExistsAsync(name, excludeId, ct));

	[HttpPost("import/preview")]
	public async Task<IActionResult> PreviewImport([FromBody] SkillImportRequest request, CancellationToken ct)
		=> await ExecuteMutationAsync(() => _skillService.PreviewImportAsync(request, ct));

	[HttpPost("import")]
	public async Task<IActionResult> Import([FromBody] SkillImportRequest request, CancellationToken ct)
		=> await ExecuteMutationAsync(() => _skillService.ImportAsync(request, ct));

    [HttpPost("expand")]
	public async Task<IActionResult> ExpandSkill([FromBody] ExpandSkillRequest request, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(request.Description))
			return BadRequest("Description is required");

        var result = await _skillService.ExpandSkillAsync(request.Description, request.ProviderId, request.ModelId, ct);
        return result != null ? Content(result, "text/plain") : BadRequest("Failed to expand skill");
    }

	public record ExpandSkillRequest(string Description, Guid ProviderId, string? ModelId);

	private async Task<IActionResult> ExecuteMutationAsync<T>(Func<Task<T>> action)
	{
		try
		{
			return Ok(await action());
		}
		catch (Exception ex) when (ex is ValidationException or InvalidOperationException or InvalidDataException)
		{
			return BadRequest(new { message = ex.Message });
		}
	}
}
