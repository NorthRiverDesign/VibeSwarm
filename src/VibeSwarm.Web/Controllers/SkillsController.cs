using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
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
    public async Task<IActionResult> Create([FromBody] Skill skill, CancellationToken ct) => Ok(await _skillService.CreateAsync(skill, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Skill skill, CancellationToken ct)
    {
        skill.Id = id;
        return Ok(await _skillService.UpdateAsync(skill, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _skillService.DeleteAsync(id, ct); return Ok(); }

    [HttpGet("name-exists")]
    public async Task<IActionResult> NameExists([FromQuery] string name, [FromQuery] Guid? excludeId = null, CancellationToken ct = default)
        => Ok(await _skillService.NameExistsAsync(name, excludeId, ct));
}
