using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
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

    [HttpGet("github-repositories")]
    public async Task<IActionResult> BrowseGitHubRepositories(CancellationToken ct)
        => Ok(await _projectService.BrowseGitHubRepositoriesAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Project project, CancellationToken ct)
    {
        try
        {
            return Ok(await _projectService.CreateAsync(project, ct));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? NotFound(new { error = ex.Message })
                : BadRequest(new { error = ex.Message });
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("FOREIGN KEY") == true ||
                                           ex.InnerException?.Message.Contains("foreign key") == true)
        {
            return BadRequest(new { error = "One or more selected providers do not exist." });
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            var inner = ex.InnerException?.Message ?? string.Empty;
            var error = inner.Contains("Projects", StringComparison.OrdinalIgnoreCase) &&
                        inner.Contains("Name", StringComparison.OrdinalIgnoreCase)
                ? "A project with this name already exists."
                : "A duplicate value was detected. Check for duplicate provider selections or environment names.";
            return BadRequest(new { error });
        }
        catch (DbUpdateException ex)
        {
            return BadRequest(new { error = ex.InnerException?.Message ?? ex.Message });
        }
    }

    [HttpPost("provision")]
    public async Task<IActionResult> CreateProject([FromBody] ProjectCreationRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await _projectService.CreateProjectAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? NotFound(new { error = ex.Message })
                : BadRequest(new { error = ex.Message });
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("FOREIGN KEY") == true ||
                                           ex.InnerException?.Message.Contains("foreign key") == true)
        {
            return BadRequest(new { error = "One or more selected providers do not exist." });
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            var inner = ex.InnerException?.Message ?? string.Empty;
            var error = inner.Contains("Projects", StringComparison.OrdinalIgnoreCase) &&
                        inner.Contains("Name", StringComparison.OrdinalIgnoreCase)
                ? "A project with this name already exists."
                : "A duplicate value was detected. Check for duplicate provider selections or environment names.";
            return BadRequest(new { error });
        }
        catch (DbUpdateException ex)
        {
            return BadRequest(new { error = ex.InnerException?.Message ?? ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Project project, CancellationToken ct)
    {
        try
        {
            project.Id = id;
            return Ok(await _projectService.UpdateAsync(project, ct));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? NotFound(new { error = ex.Message })
                : BadRequest(new { error = ex.Message });
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("FOREIGN KEY") == true ||
                                           ex.InnerException?.Message.Contains("foreign key") == true)
        {
            return BadRequest(new { error = "One or more selected providers do not exist." });
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            var inner = ex.InnerException?.Message ?? string.Empty;
            var error = inner.Contains("Projects", StringComparison.OrdinalIgnoreCase) &&
                        inner.Contains("Name", StringComparison.OrdinalIgnoreCase)
                ? "A project with this name already exists."
                : "A duplicate value was detected. Check for duplicate provider selections or environment names.";
            return BadRequest(new { error });
        }
        catch (DbUpdateException ex)
        {
            return BadRequest(new { error = ex.InnerException?.Message ?? ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _projectService.DeleteAsync(id, ct); return Ok(); }

    [HttpGet("with-stats")]
    public async Task<IActionResult> GetAllWithStats(CancellationToken ct) => Ok(await _projectService.GetAllWithStatsAsync(ct));

    [HttpGet("recent-dashboard")]
    public async Task<IActionResult> GetRecentDashboard([FromQuery] int count = 10, CancellationToken ct = default) => Ok(await _projectService.GetRecentWithLatestJobAsync(count, ct));

    [HttpGet("dashboard-metrics")]
    public async Task<IActionResult> GetDashboardMetrics([FromQuery] int rangeDays = 7, CancellationToken ct = default)
        => Ok(await _projectService.GetDashboardJobMetricsAsync(rangeDays, ct));
}
