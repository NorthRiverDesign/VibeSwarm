using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

public class ProjectService : IProjectService
{
	private static readonly HashSet<int> ValidDashboardRanges = [1, 7, 30, 90];
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IProjectEnvironmentCredentialService _credentialService;
	private readonly IVersionControlService _versionControlService;

	public ProjectService(
 		VibeSwarmDbContext dbContext,
		IProjectEnvironmentCredentialService credentialService,
		IVersionControlService versionControlService)
	{
		_dbContext = dbContext;
		_credentialService = credentialService;
		_versionControlService = versionControlService;
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

		ValidateProject(project);
		var nameExists = await _dbContext.Projects
			.AnyAsync(p => p.Name == project.Name, cancellationToken);
		if (nameExists)
		{
			throw new InvalidOperationException($"A project named '{project.Name}' already exists.");
		}
		NormalizePlanningSettings(project);
		project.DefaultTargetBranch = string.IsNullOrWhiteSpace(project.DefaultTargetBranch) ? null : project.DefaultTargetBranch.Trim();
		project.BuildCommand = string.IsNullOrWhiteSpace(project.BuildCommand) ? null : project.BuildCommand.Trim();
		project.TestCommand = string.IsNullOrWhiteSpace(project.TestCommand) ? null : project.TestCommand.Trim();
		project.Memory = NormalizeProjectMemory(project.Memory);
		NormalizeProviderSelections(project);
		NormalizeEnvironments(project);
		await ValidateProviderSelectionsAsync(project.ProviderSelections, cancellationToken);
		await ValidatePlanningAsync(project, cancellationToken);
		ValidateEnvironments(project.Environments);
		_credentialService.PrepareForStorage(project);

		_dbContext.Projects.Add(project);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(project.Id, cancellationToken) ?? project;
	}

	public async Task<Project> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(request.Project);
		ValidateProjectCreationRequest(request);

		switch (request.Mode)
		{
			case ProjectCreationMode.ExistingDirectory:
				return await CreateAsync(request.Project, cancellationToken);

			case ProjectCreationMode.CloneGitHubRepository:
				await CloneGitHubRepositoryAsync(request, cancellationToken);
				return await CreateAsync(request.Project, cancellationToken);

			case ProjectCreationMode.CreateGitHubRepository:
				await CreateGitHubRepositoryAsync(request, cancellationToken);
				return await CreateAsync(request.Project, cancellationToken);

			default:
				throw new InvalidOperationException("Unsupported project creation mode.");
		}
	}

	public async Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default)
	{
		var existing = await _dbContext.Projects
			.FirstOrDefaultAsync(p => p.Id == project.Id, cancellationToken);

		if (existing == null)
		{
			throw new InvalidOperationException($"Project with ID {project.Id} not found.");
		}

		ValidateProject(project);
		var nameExists = await _dbContext.Projects
			.AnyAsync(p => p.Name == project.Name && p.Id != project.Id, cancellationToken);
		if (nameExists)
		{
			throw new InvalidOperationException($"A project named '{project.Name}' already exists.");
		}
		NormalizePlanningSettings(project);
		existing.Name = project.Name;
		existing.Description = project.Description;
		existing.WorkingPath = project.WorkingPath;
		existing.GitHubRepository = project.GitHubRepository;
		existing.AutoCommitMode = project.AutoCommitMode;
		existing.GitChangeDeliveryMode = project.GitChangeDeliveryMode;
		existing.DefaultTargetBranch = string.IsNullOrWhiteSpace(project.DefaultTargetBranch) ? null : project.DefaultTargetBranch.Trim();
		existing.PlanningEnabled = project.PlanningEnabled;
		existing.PlanningProviderId = project.PlanningProviderId;
		existing.PlanningModelId = project.PlanningModelId;
		existing.IdeaInferenceProviderId = project.IdeaInferenceProviderId;
		existing.IdeaInferenceModelId = string.IsNullOrWhiteSpace(project.IdeaInferenceModelId) ? null : project.IdeaInferenceModelId.Trim();
		existing.PromptContext = project.PromptContext;
		existing.Memory = NormalizeProjectMemory(project.Memory);
		existing.BuildVerificationEnabled = project.BuildVerificationEnabled;
		existing.BuildCommand = string.IsNullOrWhiteSpace(project.BuildCommand) ? null : project.BuildCommand.Trim();
		existing.TestCommand = string.IsNullOrWhiteSpace(project.TestCommand) ? null : project.TestCommand.Trim();
		existing.IsActive = project.IsActive;
		existing.IdeasAutoExpand = project.IdeasAutoExpand;
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
		await ValidatePlanningAsync(project, cancellationToken);
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
			.AsNoTracking()
			.OrderByDescending(p => p.CreatedAt)
			.ToListAsync(cancellationToken);

		var projectIds = projects.Select(p => p.Id).ToList();
		var stats = await _dbContext.Jobs
			.AsNoTracking()
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

		var ideaStats = await _dbContext.Ideas
			.AsNoTracking()
			.Where(idea => projectIds.Contains(idea.ProjectId))
			.GroupBy(idea => idea.ProjectId)
			.Select(group => new
			{
				ProjectId = group.Key,
				TotalIdeas = group.Count(),
				UnprocessedIdeas = group.Count(idea => !idea.IsProcessing && idea.JobId == null)
			})
			.ToListAsync(cancellationToken);

		var latestJobs = await _dbContext.Jobs
			.AsNoTracking()
			.Where(job => projectIds.Contains(job.ProjectId))
			.GroupBy(job => job.ProjectId)
			.Select(group => group
				.OrderByDescending(job => job.CreatedAt)
				.First())
			.ToListAsync(cancellationToken);

		var statsByProject = stats.ToDictionary(s => s.ProjectId);
		var ideaStatsByProject = ideaStats.ToDictionary(
			stat => stat.ProjectId,
			stat => (stat.TotalIdeas, stat.UnprocessedIdeas));
		var latestJobsByProject = latestJobs.ToDictionary(job => job.ProjectId);
		var branchesByProject = new Dictionary<Guid, string?>();

		foreach (var project in projects)
		{
			try
			{
				if (await _versionControlService.IsGitRepositoryAsync(project.WorkingPath, cancellationToken))
				{
					branchesByProject[project.Id] = await _versionControlService.GetCurrentBranchAsync(project.WorkingPath, cancellationToken);
				}
			}
			catch
			{
				// Ignore branch lookup failures and continue returning the rest of the summary data.
			}
		}

		return projects.Select(p => new ProjectWithStats
		{
			Project = p,
			Stats = BuildProjectStats(p.Id),
			CurrentBranch = branchesByProject.GetValueOrDefault(p.Id),
			LatestJob = latestJobsByProject.GetValueOrDefault(p.Id)
		});

		ProjectJobStats BuildProjectStats(Guid projectId)
		{
			var projectStats = statsByProject.TryGetValue(projectId, out var existingStats)
				? existingStats
				: new ProjectJobStats { ProjectId = projectId };

			if (ideaStatsByProject.TryGetValue(projectId, out var projectIdeaStats))
			{
				projectStats.TotalIdeas = projectIdeaStats.TotalIdeas;
				projectStats.UnprocessedIdeas = projectIdeaStats.UnprocessedIdeas;
			}

			return projectStats;
		}
	}

	public async Task<IEnumerable<DashboardProjectInfo>> GetRecentWithLatestJobAsync(int count, CancellationToken cancellationToken = default)
	{
		if (count <= 0)
		{
			return Enumerable.Empty<DashboardProjectInfo>();
		}

		var projects = await BuildProjectQuery()
			.AsNoTracking()
			.Where(p => p.IsActive)
			.ToListAsync(cancellationToken);

		if (!projects.Any())
		{
			return Enumerable.Empty<DashboardProjectInfo>();
		}

		var projectIds = projects.Select(p => p.Id).ToList();
		var latestJobs = await _dbContext.Jobs
			.AsNoTracking()
			.Where(j => projectIds.Contains(j.ProjectId))
			.GroupBy(j => j.ProjectId)
			.Select(g => g.OrderByDescending(j => j.CreatedAt).First())
			.ToListAsync(cancellationToken);

		var latestJobsByProject = latestJobs.ToDictionary(j => j.ProjectId);

		return projects
			.Select(p => new DashboardProjectInfo
			{
				Project = p,
				LatestJob = latestJobsByProject.TryGetValue(p.Id, out var job) ? job : null
			})
			.OrderByDescending(project => project.LatestJob is not null)
			.ThenByDescending(project => project.LatestJob?.CreatedAt ?? DateTime.MinValue)
			.ThenBy(project => project.Project.Name)
			.Take(count)
			.ToList();
	}

	public async Task<DashboardJobMetrics> GetDashboardJobMetricsAsync(int rangeDays, CancellationToken cancellationToken = default)
	{
		var normalizedRangeDays = NormalizeDashboardRange(rangeDays);
		var nowUtc = DateTime.UtcNow;
		var bucketConfig = CreateDashboardBucketConfig(normalizedRangeDays, nowUtc);

		var jobRows = await _dbContext.Jobs
			.AsNoTracking()
			.Where(job => job.Status == JobStatus.Completed &&
				job.CompletedAt.HasValue &&
				job.CompletedAt.Value >= bucketConfig.StartUtc &&
				job.CompletedAt.Value < bucketConfig.EndUtc)
			.Select(job => new DashboardJobMetricsRow
			{
				CompletedAt = job.CompletedAt!.Value,
				StartedAt = job.StartedAt,
				ExecutionDurationSeconds = job.ExecutionDurationSeconds
			})
			.ToListAsync(cancellationToken);

		var buckets = Enumerable.Range(0, bucketConfig.BucketCount)
			.Select(index => new DashboardBucketAccumulator
			{
				BucketStartUtc = bucketConfig.StartUtc.AddTicks(bucketConfig.BucketSpan.Ticks * index)
			})
			.ToList();

		double durationTotalSeconds = 0;
		var durationSamples = 0;

		foreach (var row in jobRows)
		{
			var bucketIndex = (int)((row.CompletedAt - bucketConfig.StartUtc).Ticks / bucketConfig.BucketSpan.Ticks);
			if (bucketIndex < 0 || bucketIndex >= buckets.Count)
			{
				continue;
			}

			var bucket = buckets[bucketIndex];
			bucket.CompletedJobs++;

			var durationSeconds = ResolveDurationSeconds(row);
			if (!durationSeconds.HasValue)
			{
				continue;
			}

			bucket.DurationTotalSeconds += durationSeconds.Value;
			bucket.DurationSamples++;
			durationTotalSeconds += durationSeconds.Value;
			durationSamples++;
		}

		return new DashboardJobMetrics
		{
			RangeDays = normalizedRangeDays,
			StartUtc = bucketConfig.StartUtc,
			EndUtc = bucketConfig.EndUtc,
			TotalCompletedJobs = jobRows.Count,
			AverageDurationSeconds = durationSamples > 0 ? durationTotalSeconds / durationSamples : null,
			Buckets = buckets
				.Select(bucket => new DashboardJobMetricsBucket
				{
					BucketStartUtc = bucket.BucketStartUtc,
					CompletedJobs = bucket.CompletedJobs,
					AverageDurationSeconds = bucket.DurationSamples > 0
						? bucket.DurationTotalSeconds / bucket.DurationSamples
						: null
				})
				.ToList()
		};
	}

	private IQueryable<Project> BuildProjectQuery()
	{
		return _dbContext.Projects
			.Include(p => p.Environments.OrderBy(environment => environment.SortOrder))
			.Include(p => p.ProviderSelections.OrderBy(pp => pp.Priority))
				.ThenInclude(pp => pp.Provider);
	}

	private static void ValidateProject(Project project)
	{
		ValidationHelper.ValidateObject(project);
	}

	private static void ValidateProjectCreationRequest(ProjectCreationRequest request)
	{
		ValidationHelper.ValidateObject(request.Project);
		if (request.GitHub != null)
		{
			ValidationHelper.ValidateObject(request.GitHub);
		}
	}

	private async Task CloneGitHubRepositoryAsync(ProjectCreationRequest request, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(request.Project.WorkingPath))
		{
			throw new InvalidOperationException("A working path is required when cloning a GitHub repository.");
		}

		var repository = NormalizeGitHubRepository(request.GitHub?.Repository ?? request.Project.GitHubRepository, allowOwnerOnly: false);
		request.Project.GitHubRepository = repository;

		var ghAvailable = await _versionControlService.IsGitHubCliAvailableAsync(cancellationToken);
		var ghAuthenticated = ghAvailable && await _versionControlService.IsGitHubCliAuthenticatedAsync(cancellationToken);

		var cloneResult = ghAuthenticated
			? await _versionControlService.CloneWithGitHubCliAsync(repository, request.Project.WorkingPath, cancellationToken: cancellationToken)
			: await _versionControlService.CloneRepositoryAsync(
				_versionControlService.GetGitHubCloneUrl(repository, useSsh: false),
				request.Project.WorkingPath,
				cancellationToken: cancellationToken);

		if (!cloneResult.Success)
		{
			throw new InvalidOperationException(cloneResult.Error ?? "Failed to clone GitHub repository.");
		}
	}

	private async Task CreateGitHubRepositoryAsync(ProjectCreationRequest request, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(request.Project.WorkingPath))
		{
			throw new InvalidOperationException("A working path is required when creating a GitHub repository.");
		}

		var repository = NormalizeGitHubRepository(request.GitHub?.Repository, allowOwnerOnly: true);
		var result = await _versionControlService.CreateGitHubRepositoryAsync(
			request.Project.WorkingPath,
			repository,
			request.GitHub?.Description,
			request.GitHub?.IsPrivate ?? false,
			cancellationToken: cancellationToken,
			gitignoreTemplate: request.GitHub?.GitignoreTemplate,
			licenseTemplate: request.GitHub?.LicenseTemplate,
			initializeReadme: request.GitHub?.InitializeReadme ?? false);

		if (!result.Success)
		{
			throw new InvalidOperationException(result.Error ?? "Failed to create GitHub repository.");
		}

		var remoteUrl = await _versionControlService.GetRemoteUrlAsync(request.Project.WorkingPath, cancellationToken: cancellationToken);
		request.Project.GitHubRepository = _versionControlService.ExtractGitHubRepository(remoteUrl);
		if (string.IsNullOrWhiteSpace(request.Project.GitHubRepository))
		{
			throw new InvalidOperationException("GitHub repository was created, but the linked remote could not be determined.");
		}
	}

	private string NormalizeGitHubRepository(string? value, bool allowOwnerOnly)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidOperationException("A GitHub repository is required for this project creation mode.");
		}

		var trimmed = value.Trim();
		var extracted = _versionControlService.ExtractGitHubRepository(trimmed);
		if (!string.IsNullOrWhiteSpace(extracted))
		{
			trimmed = extracted;
		}

		trimmed = trimmed.Trim().Trim('/');
		if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			trimmed = trimmed[..^4];
		}

		var slashCount = trimmed.Count(character => character == '/');
		if (slashCount == 1)
		{
			var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (parts.Length == 2 &&
				!string.IsNullOrWhiteSpace(parts[0]) &&
				!string.IsNullOrWhiteSpace(parts[1]))
			{
				return $"{parts[0]}/{parts[1]}";
			}
		}

		if (allowOwnerOnly && slashCount == 0 && !string.IsNullOrWhiteSpace(trimmed))
		{
			return trimmed;
		}

		throw new InvalidOperationException(allowOwnerOnly
			? "GitHub repositories must use the format 'repo' or 'owner/repo'."
			: "GitHub repositories must use the format 'owner/repo'.");
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
			selection.PreferredModelId = string.IsNullOrWhiteSpace(selection.PreferredModelId)
				? null
				: selection.PreferredModelId.Trim();
			selection.UpdatedAt = DateTime.UtcNow;
			if (selection.CreatedAt == default)
			{
				selection.CreatedAt = DateTime.UtcNow;
			}
		}

		project.ProviderSelections = orderedSelections;
	}

	private static void NormalizePlanningSettings(Project project)
	{
		project.PlanningModelId = string.IsNullOrWhiteSpace(project.PlanningModelId)
			? null
			: project.PlanningModelId.Trim();
	}

	private static string? NormalizeProjectMemory(string? memory)
	{
		if (string.IsNullOrWhiteSpace(memory))
		{
			return null;
		}

		return memory
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace("\r", "\n", StringComparison.Ordinal)
			.Trim();
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
				_dbContext.ProjectProviders.Add(new ProjectProvider
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
				// Use DbSet.Add to explicitly mark as Added. Adding to the
				// navigation collection with a pre-set GUID key causes EF Core
				// to treat the entity as Modified (existing), not Added (new).
				_dbContext.ProjectEnvironments.Add(new ProjectEnvironment
				{
					Id = requested.Id,
					ProjectId = existing.Id,
					Name = requested.Name,
					Description = requested.Description,
					Type = requested.Type,
					Stage = requested.Stage,
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
			current.Stage = requested.Stage;
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

		var preferredModels = selections
			.Where(selection => !string.IsNullOrWhiteSpace(selection.PreferredModelId))
			.Select(selection => new
			{
				selection.ProviderId,
				ModelId = selection.PreferredModelId!.Trim()
			})
			.ToList();
		if (!preferredModels.Any())
		{
			return;
		}

		var providerModels = await _dbContext.ProviderModels
			.AsNoTracking()
			.Where(model => model.IsAvailable && providerIds.Contains(model.ProviderId))
			.Select(model => new { model.ProviderId, model.ModelId })
			.ToListAsync(cancellationToken);

		var availableModels = providerModels.ToHashSet();
		var invalidPreferredModels = preferredModels
			.Where(selection => !availableModels.Contains(new { selection.ProviderId, selection.ModelId }))
			.Select(selection => $"{selection.ProviderId}:{selection.ModelId}")
			.ToList();
		if (invalidPreferredModels.Any())
		{
			throw new InvalidOperationException($"One or more preferred project models are not available for their provider: {string.Join(", ", invalidPreferredModels)}");
		}
	}

	private async Task ValidatePlanningAsync(Project project, CancellationToken cancellationToken)
	{
		if (!project.PlanningEnabled)
		{
			return;
		}

		if (!project.PlanningProviderId.HasValue)
		{
			throw new InvalidOperationException("Planning requires selecting a provider.");
		}

		var provider = await _dbContext.Providers
			.AsNoTracking()
			.FirstOrDefaultAsync(p => p.Id == project.PlanningProviderId.Value, cancellationToken);
		if (provider == null)
		{
			throw new InvalidOperationException("The selected planning provider does not exist.");
		}

		if (!provider.IsEnabled)
		{
			throw new InvalidOperationException("The selected planning provider is disabled.");
		}

		if (provider.Type is not (ProviderType.Claude or ProviderType.Copilot))
		{
			throw new InvalidOperationException("Planning currently supports only Claude and GitHub Copilot providers.");
		}

		if (string.IsNullOrWhiteSpace(project.PlanningModelId))
		{
			return;
		}

		var modelExists = await _dbContext.ProviderModels
			.AsNoTracking()
			.AnyAsync(
				model => model.ProviderId == provider.Id &&
					model.IsAvailable &&
					model.ModelId == project.PlanningModelId,
				cancellationToken);
		if (!modelExists)
		{
			throw new InvalidOperationException("The selected planning model is not available for the chosen provider.");
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
		}
	}

	private static int NormalizeDashboardRange(int rangeDays)
	{
		return ValidDashboardRanges.Contains(rangeDays) ? rangeDays : 7;
	}

	private static DashboardBucketConfig CreateDashboardBucketConfig(int rangeDays, DateTime nowUtc)
	{
		if (rangeDays == 1)
		{
			var endUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
			return new DashboardBucketConfig
			{
				StartUtc = endUtc.AddHours(-24),
				EndUtc = endUtc,
				BucketCount = 24,
				BucketSpan = TimeSpan.FromHours(1)
			};
		}

		if (rangeDays == 90)
		{
			var endUtc = nowUtc.Date.AddDays(1);
			return new DashboardBucketConfig
			{
				StartUtc = endUtc.AddDays(-90),
				EndUtc = endUtc,
				BucketCount = 13,
				BucketSpan = TimeSpan.FromDays(7)
			};
		}

		return new DashboardBucketConfig
		{
			StartUtc = nowUtc.Date.AddDays(-(rangeDays - 1)),
			EndUtc = nowUtc.Date.AddDays(1),
			BucketCount = rangeDays,
			BucketSpan = TimeSpan.FromDays(1)
		};
	}

	private static double? ResolveDurationSeconds(DashboardJobMetricsRow row)
	{
		if (row.ExecutionDurationSeconds.HasValue && row.ExecutionDurationSeconds.Value >= 0)
		{
			return row.ExecutionDurationSeconds.Value;
		}

		if (row.StartedAt.HasValue)
		{
			var computedSeconds = (row.CompletedAt - row.StartedAt.Value).TotalSeconds;
			return computedSeconds >= 0 ? computedSeconds : null;
		}

		return null;
	}

	private sealed class DashboardBucketAccumulator
	{
		public required DateTime BucketStartUtc { get; init; }
		public int CompletedJobs { get; set; }
		public double DurationTotalSeconds { get; set; }
		public int DurationSamples { get; set; }
	}

	private sealed class DashboardJobMetricsRow
	{
		public required DateTime CompletedAt { get; init; }
		public DateTime? StartedAt { get; init; }
		public double? ExecutionDurationSeconds { get; init; }
	}

	private sealed class DashboardBucketConfig
	{
		public required DateTime StartUtc { get; init; }
		public required DateTime EndUtc { get; init; }
		public required int BucketCount { get; init; }
		public required TimeSpan BucketSpan { get; init; }
	}
}
