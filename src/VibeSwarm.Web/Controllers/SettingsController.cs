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
}
