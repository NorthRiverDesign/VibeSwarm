using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/schedules")]
[Authorize]
public class SchedulesController : ControllerBase
{
	private readonly IJobScheduleService _jobScheduleService;

	public SchedulesController(IJobScheduleService jobScheduleService)
	{
		_jobScheduleService = jobScheduleService;
	}

	[HttpGet]
	public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
		=> Ok(await _jobScheduleService.GetAllAsync(cancellationToken));

	[HttpGet("{id:guid}")]
	public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
	{
		var schedule = await _jobScheduleService.GetByIdAsync(id, cancellationToken);
		return schedule == null ? NotFound() : Ok(schedule);
	}

	[HttpPost]
	public async Task<IActionResult> Create([FromBody] JobSchedule schedule, CancellationToken cancellationToken)
	{
		try
		{
			return Ok(await _jobScheduleService.CreateAsync(schedule, cancellationToken));
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
	public async Task<IActionResult> Update(Guid id, [FromBody] JobSchedule schedule, CancellationToken cancellationToken)
	{
		try
		{
			schedule.Id = id;
			return Ok(await _jobScheduleService.UpdateAsync(schedule, cancellationToken));
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

	[HttpPut("{id:guid}/enabled")]
	public async Task<IActionResult> SetEnabled(Guid id, [FromBody] UpdateEnabledRequest request, CancellationToken cancellationToken)
	{
		try
		{
			return Ok(await _jobScheduleService.SetEnabledAsync(id, request.IsEnabled, cancellationToken));
		}
		catch (InvalidOperationException ex)
		{
			return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
				? NotFound(new { error = ex.Message })
				: BadRequest(new { error = ex.Message });
		}
	}

	[HttpDelete("{id:guid}")]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		await _jobScheduleService.DeleteAsync(id, cancellationToken);
		return Ok();
	}

	public sealed record UpdateEnabledRequest(bool IsEnabled);
}
