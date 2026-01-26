using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public class ProjectService : IProjectService
{
    private readonly VibeSwarmDbContext _dbContext;

    public ProjectService(VibeSwarmDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Projects
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Projects
            .OrderByDescending(p => p.CreatedAt)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Projects
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Projects
            .Include(p => p.Jobs)
                .ThenInclude(j => j.Provider)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default)
    {
        project.Id = Guid.NewGuid();
        project.CreatedAt = DateTime.UtcNow;

        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return project;
    }

    public async Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.Projects
            .FirstOrDefaultAsync(p => p.Id == project.Id, cancellationToken);

        if (existing == null)
        {
            throw new InvalidOperationException($"Project with ID {project.Id} not found.");
        }

        existing.Name = project.Name;
        existing.Description = project.Description;
        existing.WorkingPath = project.WorkingPath;
        existing.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return existing;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.Projects
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (project != null)
        {
            _dbContext.Projects.Remove(project);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IEnumerable<ProjectWithStats>> GetAllWithStatsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _dbContext.Projects
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        var projectIds = projects.Select(p => p.Id).ToList();

        // Get aggregated stats for all projects in one query
        var stats = await _dbContext.Jobs
            .Where(j => projectIds.Contains(j.ProjectId))
            .GroupBy(j => j.ProjectId)
            .Select(g => new ProjectJobStats
            {
                ProjectId = g.Key,
                TotalJobs = g.Count(),
                CompletedJobs = g.Count(j => j.Status == JobStatus.Completed),
                FailedJobs = g.Count(j => j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled),
                ActiveJobs = g.Count(j => j.Status == JobStatus.New || j.Status == JobStatus.Started || j.Status == JobStatus.Processing),
                TotalInputTokens = g.Sum(j => j.InputTokens ?? 0),
                TotalOutputTokens = g.Sum(j => j.OutputTokens ?? 0),
                TotalCostUsd = g.Sum(j => j.TotalCostUsd ?? 0)
            })
            .ToListAsync(cancellationToken);

        var statsByProject = stats.ToDictionary(s => s.ProjectId);

        return projects.Select(p => new ProjectWithStats
        {
            Project = p,
            Stats = statsByProject.TryGetValue(p.Id, out var s) ? s : new ProjectJobStats { ProjectId = p.Id }
        });
    }
}
