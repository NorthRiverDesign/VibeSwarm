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
            .Include(p => p.ProviderSelections.OrderBy(pp => pp.Priority))
                .ThenInclude(pp => pp.Provider)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Projects
            .Include(p => p.ProviderSelections.OrderBy(pp => pp.Priority))
                .ThenInclude(pp => pp.Provider)
            .OrderByDescending(p => p.CreatedAt)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Projects
            .Include(p => p.ProviderSelections.OrderBy(pp => pp.Priority))
                .ThenInclude(pp => pp.Provider)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Projects
            .Include(p => p.ProviderSelections.OrderBy(pp => pp.Priority))
                .ThenInclude(pp => pp.Provider)
            .Include(p => p.Jobs)
                .ThenInclude(j => j.Provider)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default)
    {
        project.Id = Guid.NewGuid();
        project.CreatedAt = DateTime.UtcNow;

        NormalizeProviderSelections(project);
        await ValidateProviderSelectionsAsync(project.ProviderSelections, cancellationToken);

        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(project.Id, cancellationToken) ?? project;
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
        existing.GitHubRepository = project.GitHubRepository;
        existing.AutoCommitMode = project.AutoCommitMode;
        existing.PromptContext = project.PromptContext;
        existing.IsActive = project.IsActive;
        existing.UpdatedAt = DateTime.UtcNow;

        await _dbContext.Entry(existing)
            .Collection(p => p.ProviderSelections)
            .LoadAsync(cancellationToken);

        await ValidateProviderSelectionsAsync(project.ProviderSelections, cancellationToken);
        SynchronizeProviderSelections(existing, project.ProviderSelections);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(existing.Id, cancellationToken) ?? existing;
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
            .Include(p => p.ProviderSelections.OrderBy(pp => pp.Priority))
                .ThenInclude(pp => pp.Provider)
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

    public async Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default)
    {
        var projects = await _dbContext.Projects
            .Where(p => p.IsActive)
            .Include(p => p.ProviderSelections.OrderBy(pp => pp.Priority))
                .ThenInclude(pp => pp.Provider)
            .OrderByDescending(p => p.CreatedAt)
            .Take(count)
            .ToListAsync(cancellationToken);

        if (!projects.Any())
        {
            return Enumerable.Empty<DashboardProjectInfo>();
        }

        var projectIds = projects.Select(p => p.Id).ToList();

        // Get the latest job for each project using a subquery approach
        var latestJobs = await _dbContext.Jobs
            .Where(j => projectIds.Contains(j.ProjectId))
            .GroupBy(j => j.ProjectId)
            .Select(g => g.OrderByDescending(j => j.CreatedAt).First())
            .ToListAsync(cancellationToken);

        var latestJobsByProject = latestJobs.ToDictionary(j => j.ProjectId);

        return projects.Select(p => new DashboardProjectInfo
        {
            Project = p,
            LatestJob = latestJobsByProject.TryGetValue(p.Id, out var job) ? job : null
        });
    }

    private static void NormalizeProviderSelections(Project project)
    {
        if (project.ProviderSelections == null || project.ProviderSelections.Count == 0)
        {
            project.ProviderSelections = [];
            return;
        }

        var orderedSelections = project.ProviderSelections
            .GroupBy(pp => pp.ProviderId)
            .Select(g => g.OrderBy(pp => pp.Priority).First())
            .OrderBy(pp => pp.Priority)
            .ToList();

        for (var index = 0; index < orderedSelections.Count; index++)
        {
            var selection = orderedSelections[index];
            selection.Id = selection.Id == Guid.Empty ? Guid.NewGuid() : selection.Id;
            selection.ProjectId = project.Id;
            selection.Priority = index;
            selection.UpdatedAt = DateTime.UtcNow;
            if (selection.CreatedAt == default)
            {
                selection.CreatedAt = DateTime.UtcNow;
            }
        }

        project.ProviderSelections = orderedSelections;
    }

    private void SynchronizeProviderSelections(Project existing, ICollection<ProjectProvider> requestedSelections)
    {
        var requestedProject = new Project
        {
            Id = existing.Id,
            ProviderSelections = requestedSelections ?? []
        };

        NormalizeProviderSelections(requestedProject);
        var requestedByProvider = requestedProject.ProviderSelections.ToDictionary(pp => pp.ProviderId);

        var selectionsToRemove = existing.ProviderSelections
            .Where(current => !requestedByProvider.ContainsKey(current.ProviderId))
            .ToList();

        foreach (var selection in selectionsToRemove)
        {
            _dbContext.ProjectProviders.Remove(selection);
        }

        foreach (var requested in requestedProject.ProviderSelections)
        {
            var current = existing.ProviderSelections.FirstOrDefault(pp => pp.ProviderId == requested.ProviderId);
            if (current == null)
            {
                existing.ProviderSelections.Add(new ProjectProvider
                {
                    Id = requested.Id,
                    ProjectId = existing.Id,
                    ProviderId = requested.ProviderId,
                    Priority = requested.Priority,
                    IsEnabled = requested.IsEnabled,
                    PreferredModelId = requested.PreferredModelId,
                    CreatedAt = requested.CreatedAt,
                    UpdatedAt = requested.UpdatedAt
                });
                continue;
            }

            current.Priority = requested.Priority;
            current.IsEnabled = requested.IsEnabled;
            current.PreferredModelId = requested.PreferredModelId;
            current.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task ValidateProviderSelectionsAsync(ICollection<ProjectProvider> selections, CancellationToken cancellationToken)
    {
        if (selections == null || !selections.Any())
        {
            return;
        }

        // Check for duplicate provider IDs
        var providerIds = selections.Select(s => s.ProviderId).ToList();
        var duplicates = providerIds.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Any())
        {
            throw new InvalidOperationException($"Duplicate provider selection detected. Provider IDs cannot be repeated.");
        }

        // Validate all provider IDs exist in database
        var existingProviderIds = await _dbContext.Providers
            .Where(p => providerIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var invalidIds = providerIds.Except(existingProviderIds).ToList();
        if (invalidIds.Any())
        {
            throw new InvalidOperationException($"One or more provider IDs do not exist: {string.Join(", ", invalidIds)}");
        }
    }
}
