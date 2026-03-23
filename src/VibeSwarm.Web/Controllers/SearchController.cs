using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/search")]
[Authorize]
public class SearchController : ControllerBase
{
    private readonly VibeSwarmDbContext _dbContext;

    public SearchController(VibeSwarmDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Ok(new GlobalSearchResult());

        var query = q.Trim();
        var items = new List<SearchResultItem>();

        var projects = await _dbContext.Projects
            .Where(p => p.Name.Contains(query) || (p.Description != null && p.Description.Contains(query)) || (p.GitHubRepository != null && p.GitHubRepository.Contains(query)))
            .OrderBy(p => p.Name)
            .Take(5)
            .Select(p => new SearchResultItem
            {
                Id = p.Id,
                Title = p.Name,
                Subtitle = p.GitHubRepository ?? p.WorkingPath,
                Type = SearchResultType.Project,
                Url = $"/projects/{p.Id}"
            })
            .ToListAsync(ct);

        items.AddRange(projects);

        var jobs = await _dbContext.Jobs
            .Include(j => j.Project)
            .Where(j => (j.Title != null && j.Title.Contains(query)) || j.GoalPrompt.Contains(query))
            .OrderByDescending(j => j.CreatedAt)
            .Take(10)
            .Select(j => new SearchResultItem
            {
                Id = j.Id,
                Title = j.Title != null && j.Title.Length > 0 ? j.Title : j.GoalPrompt,
                Subtitle = j.Project != null ? j.Project.Name : null,
                Type = SearchResultType.Job,
                Url = $"/jobs/{j.Id}",
                StatusLabel = j.Status.ToString()
            })
            .ToListAsync(ct);

        items.AddRange(jobs);

        var ideas = await _dbContext.Ideas
            .Include(i => i.Project)
            .Where(i => i.Description.Contains(query))
            .OrderByDescending(i => i.CreatedAt)
            .Take(5)
            .Select(i => new SearchResultItem
            {
                Id = i.Id,
                Title = i.Description,
                Subtitle = i.Project != null ? i.Project.Name : null,
                Type = SearchResultType.Idea,
                Url = $"/projects/{i.ProjectId}?tab=ideas",
                StatusLabel = i.ExpansionStatus.ToString()
            })
            .ToListAsync(ct);

        items.AddRange(ideas);

        return Ok(new GlobalSearchResult { Items = items });
    }
}
