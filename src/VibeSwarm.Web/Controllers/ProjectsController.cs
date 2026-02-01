using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/projects")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectService _projectService;

    public ProjectsController(IProjectService projectService) => _projectService = projectService;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await _projectService.GetAllAsync(ct));

    [HttpGet("recent")]
    public async Task<IActionResult> GetRecent([FromQuery] int count = 10, CancellationToken ct = default) => Ok(await _projectService.GetRecentAsync(count, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var project = await _projectService.GetByIdAsync(id, ct);
        return project == null ? NotFound() : Ok(project);
    }

    [HttpGet("{id:guid}/with-jobs")]
    public async Task<IActionResult> GetByIdWithJobs(Guid id, CancellationToken ct)
    {
        var project = await _projectService.GetByIdWithJobsAsync(id, ct);
        return project == null ? NotFound() : Ok(project);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Project project, CancellationToken ct) => Ok(await _projectService.CreateAsync(project, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Project project, CancellationToken ct)
    {
        project.Id = id;
        return Ok(await _projectService.UpdateAsync(project, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _projectService.DeleteAsync(id, ct); return Ok(); }

    [HttpGet("with-stats")]
    public async Task<IActionResult> GetAllWithStats(CancellationToken ct) => Ok(await _projectService.GetAllWithStatsAsync(ct));

    [HttpGet("recent-dashboard")]
    public async Task<IActionResult> GetRecentDashboard([FromQuery] int count = 10, CancellationToken ct = default) => Ok(await _projectService.GetRecentWithLatestJobAsync(count, ct));
}
