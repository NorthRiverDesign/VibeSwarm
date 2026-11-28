using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;

    public SettingsController(ISettingsService settingsService) => _settingsService = settingsService;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _settingsService.GetSettingsAsync(ct));

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] AppSettings settings, CancellationToken ct) => Ok(await _settingsService.UpdateSettingsAsync(settings, ct));

    /// <summary>How many jobs run at once across projects; admins only.</summary>
    [HttpPut("max-concurrent-jobs")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> UpdateMaxConcurrentJobs([FromBody] int maxConcurrentJobs, CancellationToken ct)
        => Ok(await _settingsService.UpdateMaxConcurrentJobsAsync(maxConcurrentJobs, ct));

    /// <summary>Who leads and who reviews on projects working as a team.</summary>
    [HttpPut("team")]
    public async Task<IActionResult> UpdateTeam([FromBody] TeamRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await _settingsService.UpdateTeamAsync(request.LeadProfileId, request.ReviewProfileId, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public sealed record TeamRequest(Guid? LeadProfileId, Guid? ReviewProfileId);
}
