using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public class ProjectService : IProjectService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IProjectEnvironmentCredentialService _credentialService;

	public ProjectService(
		VibeSwarmDbContext dbContext,
		IProjectEnvironmentCredentialService credentialService)
	{
		_dbContext = dbContext;
		_credentialService = credentialService;
	}

	public async Task<IEnumerable<Project>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await BuildProjectQuery()
			.OrderByDescending(p => p.CreatedAt)
			.ToListAsync(cancellationToken);
	}

	public async Task<IEnumerable<Project>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
	{
		return await BuildProjectQuery()
			.OrderByDescending(p => p.CreatedAt)
			.Take(count)
			.ToListAsync(cancellationToken);
	}

	public async Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var project = await BuildProjectQuery()
			.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

		_credentialService.PopulateForEditing(project);
		return project;
	}

	public async Task<Project?> GetByIdWithJobsAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var project = await BuildProjectQuery()
			.Include(p => p.Jobs)
				.ThenInclude(j => j.Provider)
			.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

		_credentialService.PopulateForEditing(project);
		return project;
	}

	public async Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default)
	{
		project.Id = Guid.NewGuid();
		project.CreatedAt = DateTime.UtcNow;

		NormalizeProviderSelections(project);
		NormalizeEnvironments(project);
		await ValidateProviderSelectionsAsync(project.ProviderSelections, cancellationToken);
		ValidateEnvironments(project.Environments);
		_credentialService.PrepareForStorage(project);

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
		await _dbContext.Entry(existing)
			.Collection(p => p.Environments)
			.LoadAsync(cancellationToken);

		NormalizeProviderSelections(project);
		NormalizeEnvironments(project);
		await ValidateProviderSelectionsAsync(project.ProviderSelections, cancellationToken);
		ValidateEnvironments(project.Environments);
		_credentialService.PrepareForStorage(project, existing.Environments.ToList());

		SynchronizeProviderSelections(existing, project.ProviderSelections);
		SynchronizeEnvironments(existing, project.Environments);

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
		var projects = await BuildProjectQuery()
			.OrderByDescending(p => p.CreatedAt)
			.ToListAsync(cancellationToken);

		var projectIds = projects.Select(p => p.Id).ToList();
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
		var projects = await BuildProjectQuery()
			.Where(p => p.IsActive)
			.OrderByDescending(p => p.CreatedAt)
			.Take(count)
			.ToListAsync(cancellationToken);

		if (!projects.Any())
		{
			return Enumerable.Empty<DashboardProjectInfo>();
		}

		var projectIds = projects.Select(p => p.Id).ToList();
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

	private IQueryable<Project> BuildProjectQuery()
	{
		return _dbContext.Projects
			.Include(p => p.Environments.OrderBy(environment => environment.SortOrder))
			.Include(p => p.ProviderSelections.OrderBy(pp => pp.Priority))
				.ThenInclude(pp => pp.Provider);
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

	private static void NormalizeEnvironments(Project project)
	{
		if (project.Environments == null || project.Environments.Count == 0)
		{
			project.Environments = [];
			return;
		}

		var now = DateTime.UtcNow;
		var normalized = project.Environments
			.Where(environment => !string.IsNullOrWhiteSpace(environment.Name) && !string.IsNullOrWhiteSpace(environment.Url))
			.ToList();

		for (var index = 0; index < normalized.Count; index++)
		{
			var environment = normalized[index];
			environment.Id = environment.Id == Guid.Empty ? Guid.NewGuid() : environment.Id;
			environment.ProjectId = project.Id;
			environment.Name = environment.Name.Trim();
			environment.Description = string.IsNullOrWhiteSpace(environment.Description)
				? null
				: environment.Description.Trim();
			environment.Url = environment.Url.Trim();
			environment.SortOrder = index;
			environment.UpdatedAt = now;
			if (environment.CreatedAt == default)
			{
				environment.CreatedAt = now;
			}
		}

		var primary = normalized.FirstOrDefault(environment => environment.IsPrimary);
		if (normalized.Count > 0 && primary == null)
		{
			normalized[0].IsPrimary = true;
			primary = normalized[0];
		}

		foreach (var environment in normalized)
		{
			environment.IsPrimary = environment.Id == primary?.Id;
		}

		project.Environments = normalized;
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

	private void SynchronizeEnvironments(Project existing, ICollection<ProjectEnvironment> requestedEnvironments)
	{
		var requestedProject = new Project
		{
			Id = existing.Id,
			Environments = requestedEnvironments ?? []
		};

		NormalizeEnvironments(requestedProject);
		var requestedById = requestedProject.Environments.ToDictionary(environment => environment.Id);

		var environmentsToRemove = existing.Environments
			.Where(current => !requestedById.ContainsKey(current.Id))
			.ToList();

		foreach (var environment in environmentsToRemove)
		{
			_dbContext.ProjectEnvironments.Remove(environment);
		}

		foreach (var requested in requestedProject.Environments)
		{
			var current = existing.Environments.FirstOrDefault(environment => environment.Id == requested.Id);
			if (current == null)
			{
				existing.Environments.Add(new ProjectEnvironment
				{
					Id = requested.Id,
					ProjectId = existing.Id,
					Name = requested.Name,
					Description = requested.Description,
					Type = requested.Type,
					Url = requested.Url,
					IsPrimary = requested.IsPrimary,
					IsEnabled = requested.IsEnabled,
					SortOrder = requested.SortOrder,
					UsernameCiphertext = requested.UsernameCiphertext,
					PasswordCiphertext = requested.PasswordCiphertext,
					CreatedAt = requested.CreatedAt,
					UpdatedAt = requested.UpdatedAt
				});
				continue;
			}

			current.Name = requested.Name;
			current.Description = requested.Description;
			current.Type = requested.Type;
			current.Url = requested.Url;
			current.IsPrimary = requested.IsPrimary;
			current.IsEnabled = requested.IsEnabled;
			current.SortOrder = requested.SortOrder;
			current.UsernameCiphertext = requested.UsernameCiphertext;
			current.PasswordCiphertext = requested.PasswordCiphertext;
			current.UpdatedAt = DateTime.UtcNow;
		}
	}

	private async Task ValidateProviderSelectionsAsync(ICollection<ProjectProvider> selections, CancellationToken cancellationToken)
	{
		if (selections == null || !selections.Any())
		{
			return;
		}

		var providerIds = selections.Select(selection => selection.ProviderId).ToList();
		var duplicates = providerIds.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
		if (duplicates.Any())
		{
			throw new InvalidOperationException("Duplicate provider selection detected. Provider IDs cannot be repeated.");
		}

		var existingProviderIds = await _dbContext.Providers
			.Where(provider => providerIds.Contains(provider.Id))
			.Select(provider => provider.Id)
			.ToListAsync(cancellationToken);

		var invalidIds = providerIds.Except(existingProviderIds).ToList();
		if (invalidIds.Any())
		{
			throw new InvalidOperationException($"One or more provider IDs do not exist: {string.Join(", ", invalidIds)}");
		}
	}

	private static void ValidateEnvironments(ICollection<ProjectEnvironment> environments)
	{
		if (environments == null || environments.Count == 0)
		{
			return;
		}

		var duplicateNames = environments
			.GroupBy(environment => environment.Name.Trim(), StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key)
			.ToList();
		if (duplicateNames.Any())
		{
			throw new InvalidOperationException($"Environment names must be unique per project: {string.Join(", ", duplicateNames)}");
		}

		foreach (var environment in environments)
		{
			if (!Uri.TryCreate(environment.Url, UriKind.Absolute, out _))
			{
				throw new InvalidOperationException($"Environment '{environment.Name}' must use an absolute URL.");
			}

			if (environment.Type != EnvironmentType.Web)
			{
				continue;
			}

			var hasUsername = !string.IsNullOrWhiteSpace(environment.Username);
			var hasPassword = !string.IsNullOrEmpty(environment.Password);
			if (hasUsername != hasPassword && !environment.HasPassword)
			{
				throw new InvalidOperationException($"Web environment '{environment.Name}' requires both a username and password.");
			}
		}
	}
}
