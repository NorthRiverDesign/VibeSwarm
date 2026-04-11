using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/job-templates")]
[Authorize]
public class JobTemplatesController : ControllerBase
{
	private readonly IJobTemplateService _jobTemplateService;

	public JobTemplatesController(IJobTemplateService jobTemplateService)
	{
		_jobTemplateService = jobTemplateService;
	}

	[HttpGet]
	public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
		=> Ok(await _jobTemplateService.GetAllAsync(cancellationToken));

	[HttpGet("{id:guid}")]
	public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
	{
		var template = await _jobTemplateService.GetByIdAsync(id, cancellationToken);
		return template == null ? NotFound() : Ok(template);
	}

	[HttpPost]
	public async Task<IActionResult> Create([FromBody] JobTemplate template, CancellationToken cancellationToken)
	{
		try
		{
			return Ok(await _jobTemplateService.CreateAsync(template, cancellationToken));
		}
		catch (InvalidOperationException ex)
		{
			return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
				? NotFound(new { error = ex.Message })
				: BadRequest(new { error = ex.Message });
		}
		catch (DbUpdateException ex)
		{
			return BadRequest(new { error = ex.InnerException?.Message ?? ex.Message });
		}
	}

	[HttpPut("{id:guid}")]
	public async Task<IActionResult> Update(Guid id, [FromBody] JobTemplate template, CancellationToken cancellationToken)
	{
		try
		{
			template.Id = id;
			return Ok(await _jobTemplateService.UpdateAsync(template, cancellationToken));
		}
		catch (InvalidOperationException ex)
		{
			return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
				? NotFound(new { error = ex.Message })
				: BadRequest(new { error = ex.Message });
		}
		catch (DbUpdateException ex)
		{
			return BadRequest(new { error = ex.InnerException?.Message ?? ex.Message });
		}
	}

	[HttpDelete("{id:guid}")]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		await _jobTemplateService.DeleteAsync(id, cancellationToken);
		return Ok();
	}

	[HttpPost("{id:guid}/use")]
	public async Task<IActionResult> IncrementUseCount(Guid id, CancellationToken cancellationToken)
	{
		try
		{
			return Ok(await _jobTemplateService.IncrementUseCountAsync(id, cancellationToken));
		}
		catch (InvalidOperationException ex)
		{
			return NotFound(new { error = ex.Message });
		}
	}
}
