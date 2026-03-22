using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public partial class IdeaService
{
	public async Task StartProcessingAsync(Guid projectId, bool autoCommit = false, CancellationToken cancellationToken = default)
	{
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
				project.IdeasAutoCommit = autoCommit;
				await _dbContext.SaveChangesAsync(cancellationToken);
				_logger.LogInformation("Started Ideas auto-processing for project {ProjectId} (AutoCommit: {AutoCommit})", projectId, autoCommit);

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
		var job = await ConvertToJobAsync(nextIdea.Id, cancellationToken);
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

		// Update the idea's project
		idea.ProjectId = targetProjectId;

		// Update sort order in new project
		var maxSortOrder = await _dbContext.Ideas
			.Where(i => i.ProjectId == targetProjectId && i.Id != ideaId)
			.MaxAsync(i => (int?)i.SortOrder, cancellationToken) ?? -1;
		idea.SortOrder = maxSortOrder + 1;

		await _dbContext.SaveChangesAsync(cancellationToken);

		return idea;
	}
}
