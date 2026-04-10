using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public partial class IdeaService
{
	public async Task StartProcessingAsync(Guid projectId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default)
	{
		var normalizedOptions = await NormalizeIdeaProcessingOptionsAsync(projectId, options, cancellationToken);

		// Use a lock to prevent race conditions when toggling processing state
		await _processingStateLock.WaitAsync(cancellationToken);
		try
		{
			var project = await _dbContext.Projects.FindAsync(new object[] { projectId }, cancellationToken);
			if (project != null)
			{
				// Check if already active (prevents duplicate start requests)
				if (project.IdeasProcessingActive)
				{
					_logger.LogDebug("Ideas auto-processing already active for project {ProjectId}", projectId);
					return;
				}

				project.IdeasProcessingActive = true;
				project.IdeasAutoCommit = normalizedOptions.AutoCommitMode != AutoCommitMode.Off;
				project.IdeasProcessingProviderId = normalizedOptions.ProviderId;
				project.IdeasProcessingModelId = normalizedOptions.ModelId;
				project.UpdatedAt = DateTime.UtcNow;
				await _dbContext.SaveChangesAsync(cancellationToken);
				_logger.LogInformation(
					"Started Ideas auto-processing for project {ProjectId} (AutoCommit: {AutoCommit}, ProviderOverride: {ProviderId}, ModelOverride: {ModelId})",
					projectId,
					project.IdeasAutoCommit,
					project.IdeasProcessingProviderId,
					project.IdeasProcessingModelId);

				// Notify all clients about the state change
				if (_jobUpdateService != null)
				{
					try
					{
						await _jobUpdateService.NotifyIdeasProcessingStateChanged(projectId, true);
					}
					catch { /* Don't fail if notification fails */ }
				}
			}
		}
		finally
		{
			_processingStateLock.Release();
		}
	}

	public async Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		// Use a lock to prevent race conditions when toggling processing state
		await _processingStateLock.WaitAsync(cancellationToken);
		try
		{
			var project = await _dbContext.Projects.FindAsync(new object[] { projectId }, cancellationToken);
			if (project != null)
			{
				// Check if already stopped (prevents duplicate stop requests)
				if (!project.IdeasProcessingActive)
				{
					_logger.LogDebug("Ideas auto-processing already stopped for project {ProjectId}", projectId);
					return;
				}

				project.IdeasProcessingActive = false;
				project.IdeasProcessingProviderId = null;
				project.IdeasProcessingModelId = null;
				project.UpdatedAt = DateTime.UtcNow;
				await _dbContext.SaveChangesAsync(cancellationToken);
				_logger.LogInformation("Stopped Ideas auto-processing for project {ProjectId}", projectId);

				// Notify all clients about the state change
				if (_jobUpdateService != null)
				{
					try
					{
						await _jobUpdateService.NotifyIdeasProcessingStateChanged(projectId, false);
					}
					catch { /* Don't fail if notification fails */ }
				}
			}
		}
		finally
		{
			_processingStateLock.Release();
		}
	}

	public async Task<bool> IsProcessingActiveAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var project = await _dbContext.Projects
			.AsNoTracking()
			.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

		return project?.IdeasProcessingActive ?? false;
	}

	public async Task<bool> ProcessNextIdeaIfReadyAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		// Check if processing is still active
		var project = await _dbContext.Projects
			.AsNoTracking()
			.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

		if (project == null || !project.IdeasProcessingActive)
		{
			return false;
		}

		// Check if there's already an idea being processed (has a linked job that's not completed)
		var processingIdea = await _dbContext.Ideas
			.Include(i => i.Job)
			.Where(i => i.ProjectId == projectId && i.IsProcessing && i.JobId != null)
			.FirstOrDefaultAsync(cancellationToken);

		if (processingIdea != null)
		{
			// Check if the job is still running
			if (processingIdea.Job != null &&
				processingIdea.Job.Status != JobStatus.Completed &&
				processingIdea.Job.Status != JobStatus.Failed &&
				processingIdea.Job.Status != JobStatus.Cancelled)
			{
				// Still processing the current idea
				return false;
			}

			// Job completed/failed/cancelled - check if we should clean up
			if (processingIdea.Job?.Status == JobStatus.Completed)
			{
				// Remove the completed idea
				_dbContext.Ideas.Remove(processingIdea);
				await _dbContext.SaveChangesAsync(cancellationToken);
				_logger.LogInformation("Removed completed Idea {IdeaId}", processingIdea.Id);
			}
			else
			{
				// Job failed or was cancelled - reset the idea for potential retry
				processingIdea.IsProcessing = false;
				processingIdea.JobId = null;
				await _dbContext.SaveChangesAsync(cancellationToken);
				_logger.LogInformation("Reset failed Idea {IdeaId} for retry", processingIdea.Id);

				// If the job was cancelled, stop auto-processing to avoid repeat failures
				if (processingIdea.Job?.Status == JobStatus.Cancelled)
				{
					await StopProcessingAsync(projectId, cancellationToken);
					_logger.LogInformation("Stopped Ideas auto-processing for project {ProjectId} because Idea job was cancelled", projectId);
					return false;
				}
			}
		}

		// Only advance the ideas queue when the project has no other incomplete jobs.
		// This keeps auto-processing aligned with the one-job-per-project execution model.
		var hasIncompleteProjectJobs = await _dbContext.Jobs
			.AsNoTracking()
			.AnyAsync(j => j.ProjectId == projectId
				&& j.Status != JobStatus.Completed
				&& j.Status != JobStatus.Failed
				&& j.Status != JobStatus.Cancelled,
				cancellationToken);

		if (hasIncompleteProjectJobs)
		{
			_logger.LogDebug("Ideas queue for project {ProjectId} is waiting for an existing job to finish", projectId);
			return false;
		}

		// Resolve the provider this project would use and ensure it has no active jobs.
		// This limits each provider to one idea job at a time, preventing rate-limit overload
		// when multiple projects target the same provider.
		var processingOptions = new IdeaProcessingOptions
		{
			AutoCommitMode = project.IdeasAutoCommit ? AutoCommitMode.CommitOnly : AutoCommitMode.Off,
			ProviderId = project.IdeasProcessingProviderId,
			ModelId = project.IdeasProcessingModelId
		};

		var targetProvider = await ResolveJobProviderAsync(projectId, processingOptions, cancellationToken);
		if (targetProvider != null)
		{
			var providerHasActiveJob = await _dbContext.Jobs
				.AsNoTracking()
				.AnyAsync(j => j.ProviderId == targetProvider.Id
					&& j.Status != JobStatus.Completed
					&& j.Status != JobStatus.Failed
					&& j.Status != JobStatus.Cancelled,
					cancellationToken);

			if (providerHasActiveJob)
			{
				_logger.LogDebug("Provider {ProviderId} already has an active job, deferring ideas for project {ProjectId}", targetProvider.Id, projectId);
				return false;
			}
		}

		// Get the next unprocessed idea
		var nextIdea = await GetNextUnprocessedAsync(projectId, cancellationToken);
		if (nextIdea == null)
		{
			// No more ideas to process - stop processing
			await StopProcessingAsync(projectId, cancellationToken);
			_logger.LogInformation("No more Ideas to process for project {ProjectId}, stopping", projectId);
			return false;
		}

		// Convert the idea to a job
		var job = await ConvertToJobAsync(nextIdea.Id, processingOptions, cancellationToken);
		return job != null;
	}

	public async Task<IEnumerable<Guid>> GetActiveProcessingProjectsAsync(CancellationToken cancellationToken = default)
	{
		return await _dbContext.Projects
			.Where(p => p.IdeasProcessingActive)
			.Select(p => p.Id)
			.ToListAsync(cancellationToken);
	}

	public async Task ReorderIdeasAsync(Guid projectId, IEnumerable<Guid> ideaIdsInOrder, CancellationToken cancellationToken = default)
	{
		var ideas = await _dbContext.Ideas
			.Where(i => i.ProjectId == projectId)
			.ToListAsync(cancellationToken);

		var orderList = ideaIdsInOrder.ToList();
		for (int i = 0; i < orderList.Count; i++)
		{
			var idea = ideas.FirstOrDefault(x => x.Id == orderList[i]);
			if (idea != null)
			{
				idea.SortOrder = i;
			}
		}

		var project = await _dbContext.Projects.FindAsync(new object[] { projectId }, cancellationToken);
		if (project != null)
		{
			project.UpdatedAt = DateTime.UtcNow;
		}

		await _dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task<Idea> CopyToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default)
	{
		var sourceIdea = await _dbContext.Ideas.FindAsync(new object[] { ideaId }, cancellationToken);
		if (sourceIdea == null)
		{
			throw new InvalidOperationException($"Idea with ID {ideaId} not found.");
		}

		// Verify target project exists
		var targetProject = await _dbContext.Projects.FindAsync(new object[] { targetProjectId }, cancellationToken);
		if (targetProject == null)
		{
			throw new InvalidOperationException($"Target project with ID {targetProjectId} not found.");
		}

		// Create a copy of the idea in the target project
		var newIdea = new Idea
		{
			Description = sourceIdea.Description,
			ProjectId = targetProjectId
		};

		targetProject.UpdatedAt = DateTime.UtcNow;

		return await CreateAsync(newIdea, cancellationToken);
	}

	public async Task<Idea> MoveToProjectAsync(Guid ideaId, Guid targetProjectId, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas.FindAsync(new object[] { ideaId }, cancellationToken);
		if (idea == null)
		{
			throw new InvalidOperationException($"Idea with ID {ideaId} not found.");
		}

		// Cannot move ideas that are currently processing
		if (idea.IsProcessing)
		{
			throw new InvalidOperationException("Cannot move an idea that is currently being processed.");
		}

		// Verify target project exists
		var targetProject = await _dbContext.Projects.FindAsync(new object[] { targetProjectId }, cancellationToken);
		if (targetProject == null)
		{
			throw new InvalidOperationException($"Target project with ID {targetProjectId} not found.");
		}

		var sourceProjectId = idea.ProjectId;

		// Update the idea's project
		idea.ProjectId = targetProjectId;
		var now = DateTime.UtcNow;
		targetProject.UpdatedAt = now;
		var sourceProject = await _dbContext.Projects.FindAsync(new object[] { sourceProjectId }, cancellationToken);
		if (sourceProject != null)
		{
			sourceProject.UpdatedAt = now;
		}

		// Update sort order in new project
		var maxSortOrder = await _dbContext.Ideas
			.Where(i => i.ProjectId == targetProjectId && i.Id != ideaId)
			.MaxAsync(i => (int?)i.SortOrder, cancellationToken) ?? -1;
		idea.SortOrder = maxSortOrder + 1;

		await _dbContext.SaveChangesAsync(cancellationToken);

		return idea;
	}

	public async Task<GlobalIdeasProcessingStatus> GetGlobalProcessingStatusAsync(CancellationToken cancellationToken = default)
	{
		var projectsWithIdeas = await _dbContext.Projects
			.AsNoTracking()
			.Where(p => p.IsActive && p.Ideas.Any())
			.Select(p => new ProjectIdeasSummary
			{
				ProjectId = p.Id,
				ProjectName = p.Name,
				UnprocessedIdeas = p.Ideas.Count(i => !i.IsProcessing && !i.JobId.HasValue),
				QueuedIdeas = p.Ideas.Count(i => i.IsProcessing && i.Job != null && i.Job.Status == JobStatus.New),
				HasRunningJob = p.Ideas.Any(i => i.Job != null &&
					(i.Job.Status == JobStatus.Started
						|| i.Job.Status == JobStatus.Planning
						|| i.Job.Status == JobStatus.Processing)),
				IsProcessing = p.IdeasProcessingActive
			})
			.ToListAsync(cancellationToken);

		return new GlobalIdeasProcessingStatus
		{
			TotalProjectsWithIdeas = projectsWithIdeas.Count,
			TotalUnprocessedIdeas = projectsWithIdeas.Sum(p => p.UnprocessedIdeas),
			TotalQueuedIdeas = projectsWithIdeas.Sum(p => p.QueuedIdeas),
			ProjectsCurrentlyProcessing = projectsWithIdeas.Count(p => p.IsProcessing),
			Projects = projectsWithIdeas
		};
	}

	public async Task<GlobalQueueSnapshot> GetGlobalQueueSnapshotAsync(CancellationToken cancellationToken = default)
	{
		const int maxItemsPerSection = 25;

		var runningJobsQuery = _dbContext.Jobs
			.AsNoTracking()
			.Where(j => j.Status == JobStatus.Started || j.Status == JobStatus.Planning || j.Status == JobStatus.Processing)
			.OrderByDescending(j => j.StartedAt ?? j.CreatedAt);

		var upcomingIdeasQuery = _dbContext.Ideas
			.AsNoTracking()
			.Where(i => i.Project != null
				&& i.Project.IsActive
				&& ((!i.IsProcessing && !i.JobId.HasValue)
					|| (i.IsProcessing && i.Job != null && i.Job.Status == JobStatus.New)))
			.OrderByDescending(i => i.Project!.IdeasProcessingActive)
			.ThenByDescending(i => i.Project!.UpdatedAt ?? i.Project!.CreatedAt)
			.ThenBy(i => i.Project!.Name)
			.ThenBy(i => i.SortOrder)
			.ThenBy(i => i.CreatedAt);

		var runningJobsTask = runningJobsQuery
			.Select(j => new GlobalQueueJobSummary
			{
				Id = j.Id,
				ProjectId = j.ProjectId,
				ProjectName = j.Project != null ? j.Project.Name : string.Empty,
				Title = j.Title,
				GoalPrompt = j.GoalPrompt,
				Status = j.Status,
				ProviderName = j.Provider != null ? j.Provider.Name : null,
				CurrentActivity = j.CurrentActivity,
				CreatedAt = j.CreatedAt,
				StartedAt = j.StartedAt
			})
			.Take(maxItemsPerSection)
			.ToListAsync(cancellationToken);

		var runningJobsCountTask = runningJobsQuery.CountAsync(cancellationToken);

		var upcomingIdeasTask = upcomingIdeasQuery
			.Select(i => new GlobalQueueIdeaSummary
			{
				IdeaId = i.Id,
				ProjectId = i.ProjectId,
				ProjectName = i.Project != null ? i.Project.Name : string.Empty,
				Description = i.Description,
				SortOrder = i.SortOrder,
				CreatedAt = i.CreatedAt,
				IsProjectProcessing = i.Project != null && i.Project.IdeasProcessingActive,
				HasQueuedJob = i.Job != null && i.Job.Status == JobStatus.New
			})
			.Take(maxItemsPerSection)
			.ToListAsync(cancellationToken);

		var upcomingIdeasCountTask = upcomingIdeasQuery.CountAsync(cancellationToken);
		var projectsCurrentlyProcessingTask = _dbContext.Projects
			.AsNoTracking()
			.CountAsync(p => p.IsActive && p.IdeasProcessingActive, cancellationToken);

		await Task.WhenAll(
			runningJobsTask,
			runningJobsCountTask,
			upcomingIdeasTask,
			upcomingIdeasCountTask,
			projectsCurrentlyProcessingTask);

		return new GlobalQueueSnapshot
		{
			RunningJobsCount = runningJobsCountTask.Result,
			UpcomingIdeasCount = upcomingIdeasCountTask.Result,
			ProjectsCurrentlyProcessing = projectsCurrentlyProcessingTask.Result,
			RunningJobs = runningJobsTask.Result,
			UpcomingIdeas = upcomingIdeasTask.Result
		};
	}

	public async Task StartAllProcessingAsync(IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default)
	{
		// Get all active projects that have unprocessed ideas and are not already processing
		var projectIds = await _dbContext.Projects
			.AsNoTracking()
			.Where(p => p.IsActive && !p.IdeasProcessingActive &&
				p.Ideas.Any(i => !i.IsProcessing && !i.JobId.HasValue))
			.Select(p => p.Id)
			.ToListAsync(cancellationToken);

		var normalizedOptions = options ?? new IdeaProcessingOptions();
		_logger.LogInformation("Starting Ideas auto-processing for {Count} projects (AutoCommitMode: {AutoCommitMode})", projectIds.Count, normalizedOptions.AutoCommitMode);

		foreach (var projectId in projectIds)
		{
			await StartProcessingAsync(projectId, normalizedOptions, cancellationToken);
		}
	}

	public async Task StopAllProcessingAsync(CancellationToken cancellationToken = default)
	{
		var projectIds = await _dbContext.Projects
			.AsNoTracking()
			.Where(p => p.IdeasProcessingActive)
			.Select(p => p.Id)
			.ToListAsync(cancellationToken);

		_logger.LogInformation("Stopping Ideas auto-processing for {Count} projects", projectIds.Count);

		foreach (var projectId in projectIds)
		{
			await StopProcessingAsync(projectId, cancellationToken);
		}
	}

	public async Task RecoverStuckIdeasAsync(CancellationToken cancellationToken = default)
	{
		var stuckIdeas = await _dbContext.Ideas
			.Include(i => i.Job)
			.Where(i => i.IsProcessing)
			.ToListAsync(cancellationToken);

		var recovered = 0;
		foreach (var idea in stuckIdeas)
		{
			var jobIsTerminal = idea.Job == null ||
				idea.Job.Status == JobStatus.Completed ||
				idea.Job.Status == JobStatus.Failed ||
				idea.Job.Status == JobStatus.Cancelled;

			if (jobIsTerminal)
			{
				idea.IsProcessing = false;
				recovered++;
			}
		}

		if (recovered > 0)
		{
			await _dbContext.SaveChangesAsync(cancellationToken);
			_logger.LogInformation("Recovered {Count} stuck ideas", recovered);
		}
	}

	private async Task<IdeaProcessingOptions> NormalizeIdeaProcessingOptionsAsync(Guid projectId, IdeaProcessingOptions? options, CancellationToken cancellationToken)
	{
		var normalized = new IdeaProcessingOptions
		{
			AutoCommitMode = options?.AutoCommitMode ?? AutoCommitMode.Off,
			ProviderId = options?.ProviderId,
			ModelId = string.IsNullOrWhiteSpace(options?.ModelId) ? null : options!.ModelId!.Trim()
		};

		if (!normalized.ProviderId.HasValue)
		{
			if (normalized.ModelId != null)
			{
				throw new ValidationException("Choose a provider before selecting a model for queued ideas.");
			}

			return normalized;
		}

		var providerId = normalized.ProviderId.Value;
		var projectSelectionExists = await _dbContext.ProjectProviders
			.AsNoTracking()
			.AnyAsync(selection => selection.ProjectId == projectId && selection.IsEnabled, cancellationToken);

		var providerIsAllowed = projectSelectionExists
			? await _dbContext.ProjectProviders
				.AsNoTracking()
				.Where(selection => selection.ProjectId == projectId && selection.IsEnabled && selection.ProviderId == providerId)
				.Join(
					_dbContext.Providers.AsNoTracking().Where(provider => provider.IsEnabled),
					selection => selection.ProviderId,
					provider => provider.Id,
					(_, _) => true)
				.AnyAsync(cancellationToken)
			: await _dbContext.Providers
				.AsNoTracking()
				.AnyAsync(provider => provider.Id == providerId && provider.IsEnabled, cancellationToken);

		if (!providerIsAllowed)
		{
			throw new ValidationException("The selected provider is not available for this project's idea queue.");
		}

		if (normalized.ModelId == null)
		{
			return normalized;
		}

		var modelIsAvailable = await _dbContext.ProviderModels
			.AsNoTracking()
			.AnyAsync(model => model.ProviderId == providerId
				&& model.IsAvailable
				&& model.ModelId == normalized.ModelId,
				cancellationToken);

		if (!modelIsAvailable)
		{
			throw new ValidationException("The selected model is not available for the chosen provider.");
		}

		return normalized;
	}
}
