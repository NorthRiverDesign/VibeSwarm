using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

public class IdeaService : IIdeaService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IJobService _jobService;
	private readonly IProviderService _providerService;
	private readonly IVersionControlService _versionControlService;
	private readonly IJobUpdateService? _jobUpdateService;
	private readonly ILogger<IdeaService> _logger;

	/// <summary>
	/// Global lock to prevent race conditions when converting ideas to jobs
	/// </summary>
	private static readonly SemaphoreSlim _ideaConversionLock = new(1, 1);

	/// <summary>
	/// Lock for toggling ideas processing state
	/// </summary>
	private static readonly SemaphoreSlim _processingStateLock = new(1, 1);

	public IdeaService(
		VibeSwarmDbContext dbContext,
		IJobService jobService,
		IProviderService providerService,
		IVersionControlService versionControlService,
		ILogger<IdeaService> logger,
		IJobUpdateService? jobUpdateService = null)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_providerService = providerService;
		_versionControlService = versionControlService;
		_logger = logger;
		_jobUpdateService = jobUpdateService;
	}

	public async Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Where(i => i.ProjectId == projectId)
			.OrderBy(i => i.SortOrder)
			.ThenBy(i => i.CreatedAt)
			.ToListAsync(cancellationToken);
	}

	public async Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Include(i => i.Project)
			.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
	}

	public async Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default)
	{
		idea.Id = Guid.NewGuid();
		idea.CreatedAt = DateTime.UtcNow;

		// Set sort order to the next available value
		var maxSortOrder = await _dbContext.Ideas
			.Where(i => i.ProjectId == idea.ProjectId)
			.MaxAsync(i => (int?)i.SortOrder, cancellationToken) ?? -1;
		idea.SortOrder = maxSortOrder + 1;

		_dbContext.Ideas.Add(idea);
		await _dbContext.SaveChangesAsync(cancellationToken);

		// Notify all clients about the new idea
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaCreated(idea.Id, idea.ProjectId);
			}
			catch { /* Don't fail creation if notification fails */ }
		}

		return idea;
	}

	public async Task<Idea> UpdateAsync(Idea idea, CancellationToken cancellationToken = default)
	{
		var existing = await _dbContext.Ideas.FindAsync(new object[] { idea.Id }, cancellationToken);
		if (existing == null)
		{
			throw new InvalidOperationException($"Idea with ID {idea.Id} not found.");
		}

		existing.Description = idea.Description;
		existing.SortOrder = idea.SortOrder;

		await _dbContext.SaveChangesAsync(cancellationToken);

		// Notify all clients about the update
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaUpdated(existing.Id, existing.ProjectId);
			}
			catch { /* Don't fail update if notification fails */ }
		}

		return existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas.FindAsync(new object[] { id }, cancellationToken);
		if (idea != null)
		{
			var projectId = idea.ProjectId;
			_dbContext.Ideas.Remove(idea);
			await _dbContext.SaveChangesAsync(cancellationToken);

			// Notify all clients about the deletion
			if (_jobUpdateService != null)
			{
				try
				{
					await _jobUpdateService.NotifyIdeaDeleted(id, projectId);
				}
				catch { /* Don't fail deletion if notification fails */ }
			}
		}
	}

	public async Task<Idea?> GetNextUnprocessedAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Where(i => i.ProjectId == projectId && !i.IsProcessing && i.JobId == null)
			.OrderBy(i => i.SortOrder)
			.ThenBy(i => i.CreatedAt)
			.FirstOrDefaultAsync(cancellationToken);
	}

	public async Task<Job?> ConvertToJobAsync(Guid ideaId, CancellationToken cancellationToken = default)
	{
		// Use a lock to prevent race conditions when multiple users click "Start" simultaneously
		await _ideaConversionLock.WaitAsync(cancellationToken);
		try
		{
			// Re-fetch the idea inside the lock to ensure we have the latest state
			var idea = await _dbContext.Ideas
				.Include(i => i.Project)
				.FirstOrDefaultAsync(i => i.Id == ideaId, cancellationToken);

			if (idea?.Project == null)
			{
				_logger.LogWarning("Idea {IdeaId} not found or has no project", ideaId);
				return null;
			}

			// Prevent double-add: if idea already has a job, return null
			// This check inside the lock prevents race conditions
			if (idea.JobId.HasValue || idea.IsProcessing)
			{
				_logger.LogWarning("Idea {IdeaId} is already being processed or has a job (caught by lock)", ideaId);
				return null;
			}

			// Mark as processing immediately to prevent other requests
			idea.IsProcessing = true;
			await _dbContext.SaveChangesAsync(cancellationToken);

			// Notify clients immediately that this idea is now being processed
			if (_jobUpdateService != null)
			{
				try
				{
					await _jobUpdateService.NotifyIdeaUpdated(idea.Id, idea.ProjectId);
				}
				catch { /* Don't fail if notification fails */ }
			}

			// Get the default provider
			var defaultProvider = await _providerService.GetDefaultAsync(cancellationToken);
			if (defaultProvider == null)
			{
				_logger.LogWarning("No default provider configured. Cannot convert idea to job.");
				// Reset processing state on failure
				idea.IsProcessing = false;
				await _dbContext.SaveChangesAsync(cancellationToken);
				return null;
			}

			// Get the default model for the provider
			var models = await _providerService.GetModelsAsync(defaultProvider.Id, cancellationToken);
			var defaultModel = models.FirstOrDefault(m => m.IsDefault && m.IsAvailable)?.ModelId;

			// Get the current branch if it's a git repository
			string? currentBranch = null;
			try
			{
				var isGitRepo = await _versionControlService.IsGitRepositoryAsync(idea.Project.WorkingPath);
				if (isGitRepo)
				{
					currentBranch = await _versionControlService.GetCurrentBranchAsync(idea.Project.WorkingPath);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not get current branch for project {ProjectId}", idea.ProjectId);
			}

			// Use expanded description if approved, otherwise build a prompt
			var goalPrompt = idea.HasExpandedDescription
				? BuildPromptFromExpanded(idea.Description, idea.ExpandedDescription!)
				: BuildExpandedPrompt(idea.Description);

			// Create the job with the original idea as the title
			var job = new Job
			{
				ProjectId = idea.ProjectId,
				ProviderId = defaultProvider.Id,
				Title = idea.Description,  // Use the original idea text as the title
				GoalPrompt = goalPrompt,
				ModelUsed = defaultModel,
				Branch = currentBranch,
				Status = JobStatus.New
			};

			var createdJob = await _jobService.CreateAsync(job, cancellationToken);

			// Link the idea to the job
			idea.JobId = createdJob.Id;
			await _dbContext.SaveChangesAsync(cancellationToken);

			_logger.LogInformation("Converted Idea {IdeaId} to Job {JobId}", ideaId, createdJob.Id);

			// Notify all clients that this idea has started processing
			if (_jobUpdateService != null)
			{
				try
				{
					await _jobUpdateService.NotifyIdeaStarted(idea.Id, idea.ProjectId, createdJob.Id);
				}
				catch { /* Don't fail if notification fails */ }
			}

			return createdJob;
		}
		finally
		{
			_ideaConversionLock.Release();
		}
	}

	private static string BuildPromptFromExpanded(string originalIdea, string expandedDescription)
	{
		return $@"You are implementing a feature based on the following specification. This specification was reviewed and approved by the user.

## Original Idea
{originalIdea}

## Detailed Specification
{expandedDescription}

## Instructions
1. Implement the feature according to the specification above
2. Handle edge cases and error scenarios as described
3. Follow the existing code patterns and style in the project
4. Ensure the implementation is complete and functional

Implement this feature now.";
	}

	private static string BuildExpandedPrompt(string ideaDescription)
	{
		return $@"You are implementing a feature based on the following idea. First, take a moment to understand the idea and expand it into a complete feature specification. Consider edge cases, user experience, and implementation details. Then implement the feature.

## Feature Idea
{ideaDescription}

## Instructions
1. Analyze the idea and identify all the components needed
2. Consider edge cases and error handling
3. Think about the user experience and how users will interact with this feature
4. Implement the feature completely, including any necessary tests
5. Make sure the implementation follows the existing code patterns and style in the project

Begin by expanding this idea into a detailed specification, then implement it.";
	}

	public async Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas
			.FirstOrDefaultAsync(i => i.JobId == jobId, cancellationToken);

		if (idea == null)
		{
			return false;
		}

		_logger.LogInformation("Removing completed Idea {IdeaId} after Job {JobId} completed", idea.Id, jobId);

		_dbContext.Ideas.Remove(idea);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return true;
	}

	public async Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas
			.FirstOrDefaultAsync(i => i.JobId == jobId, cancellationToken);

		if (idea == null)
		{
			return false;
		}

		if (success)
		{
			// Job completed successfully - remove the idea
			_logger.LogInformation("Removing completed Idea {IdeaId} after Job {JobId} completed successfully", idea.Id, jobId);
			_dbContext.Ideas.Remove(idea);
		}
		else
		{
			// Job failed or was cancelled - reset the idea for potential retry
			_logger.LogInformation("Resetting Idea {IdeaId} after Job {JobId} failed/cancelled", idea.Id, jobId);
			idea.IsProcessing = false;
			idea.JobId = null;
		}

		await _dbContext.SaveChangesAsync(cancellationToken);

		// Notify clients about the idea state change
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaUpdated(idea.Id, idea.ProjectId);
			}
			catch { /* Don't fail if notification fails */ }
		}

		return true;
	}

	public async Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.FirstOrDefaultAsync(i => i.JobId == jobId, cancellationToken);
	}

	public async Task StartProcessingAsync(Guid projectId, CancellationToken cancellationToken = default)
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
				await _dbContext.SaveChangesAsync(cancellationToken);
				_logger.LogInformation("Started Ideas auto-processing for project {ProjectId}", projectId);

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

	public async Task<Idea?> ExpandIdeaAsync(Guid ideaId, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas
			.Include(i => i.Project)
			.FirstOrDefaultAsync(i => i.Id == ideaId, cancellationToken);

		if (idea?.Project == null)
		{
			_logger.LogWarning("Idea {IdeaId} not found or has no project", ideaId);
			return null;
		}

		// Cannot expand if already processing
		if (idea.IsProcessing || idea.JobId.HasValue)
		{
			_logger.LogWarning("Cannot expand idea {IdeaId} - already has a job or is processing", ideaId);
			return idea;
		}

		// Mark as expanding
		idea.ExpansionStatus = IdeaExpansionStatus.Expanding;
		idea.ExpansionError = null;
		await _dbContext.SaveChangesAsync(cancellationToken);

		// Notify clients about the expansion starting
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaUpdated(idea.Id, idea.ProjectId);
			}
			catch { /* Don't fail if notification fails */ }
		}

		try
		{
			// Get the default provider for AI expansion
			var defaultProvider = await _providerService.GetDefaultAsync(cancellationToken);
			if (defaultProvider == null)
			{
				idea.ExpansionStatus = IdeaExpansionStatus.Failed;
				idea.ExpansionError = "No default provider configured";
				await _dbContext.SaveChangesAsync(cancellationToken);
				await NotifyIdeaUpdateSafe(idea.Id, idea.ProjectId);
				return idea;
			}

			// Create provider instance
			var providerInstance = _providerService.CreateInstance(defaultProvider);
			if (providerInstance == null)
			{
				idea.ExpansionStatus = IdeaExpansionStatus.Failed;
				idea.ExpansionError = "Could not create provider instance";
				await _dbContext.SaveChangesAsync(cancellationToken);
				await NotifyIdeaUpdateSafe(idea.Id, idea.ProjectId);
				return idea;
			}

			// Build the expansion prompt
			var expansionPrompt = BuildIdeaExpansionPrompt(idea.Description);

			// Call the provider for a simple text response
			var response = await providerInstance.GetPromptResponseAsync(
				expansionPrompt,
				idea.Project.WorkingPath,
				cancellationToken);

			if (response.Success && !string.IsNullOrWhiteSpace(response.Response))
			{
				idea.ExpandedDescription = response.Response.Trim();
				idea.ExpansionStatus = IdeaExpansionStatus.PendingReview;
				idea.ExpandedAt = DateTime.UtcNow;
				_logger.LogInformation("Successfully expanded idea {IdeaId}", ideaId);
			}
			else
			{
				idea.ExpansionStatus = IdeaExpansionStatus.Failed;
				idea.ExpansionError = response.ErrorMessage ?? "No response from provider";
				_logger.LogWarning("Failed to expand idea {IdeaId}: {Error}", ideaId, idea.ExpansionError);
			}
		}
		catch (Exception ex)
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = ex.Message;
			_logger.LogError(ex, "Error expanding idea {IdeaId}", ideaId);
		}

		await _dbContext.SaveChangesAsync(cancellationToken);
		await NotifyIdeaUpdateSafe(idea.Id, idea.ProjectId);
		return idea;
	}

	/// <summary>
	/// Helper to notify clients about idea updates without failing the main operation
	/// </summary>
	private async Task NotifyIdeaUpdateSafe(Guid ideaId, Guid projectId)
	{
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaUpdated(ideaId, projectId);
			}
			catch { /* Don't fail if notification fails */ }
		}
	}

	public async Task<Idea?> ApproveExpansionAsync(Guid ideaId, string? editedDescription = null, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas.FindAsync(new object[] { ideaId }, cancellationToken);
		if (idea == null)
		{
			return null;
		}

		// Can only approve if pending review or if user is providing their own edit
		if (idea.ExpansionStatus != IdeaExpansionStatus.PendingReview && string.IsNullOrWhiteSpace(editedDescription))
		{
			_logger.LogWarning("Cannot approve idea {IdeaId} - not pending review", ideaId);
			return idea;
		}

		// Use edited description if provided, otherwise keep the AI-generated one
		if (!string.IsNullOrWhiteSpace(editedDescription))
		{
			idea.ExpandedDescription = editedDescription.Trim();
		}

		idea.ExpansionStatus = IdeaExpansionStatus.Approved;
		idea.ExpandedAt = DateTime.UtcNow;
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Approved expansion for idea {IdeaId}", ideaId);

		// Notify clients about the approval
		await NotifyIdeaUpdateSafe(idea.Id, idea.ProjectId);

		return idea;
	}

	public async Task<Idea?> RejectExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas.FindAsync(new object[] { ideaId }, cancellationToken);
		if (idea == null)
		{
			return null;
		}

		var projectId = idea.ProjectId;

		// Reset expansion state
		idea.ExpandedDescription = null;
		idea.ExpansionStatus = IdeaExpansionStatus.NotExpanded;
		idea.ExpansionError = null;
		idea.ExpandedAt = null;
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Rejected expansion for idea {IdeaId}", ideaId);

		// Notify clients about the rejection
		await NotifyIdeaUpdateSafe(ideaId, projectId);

		return idea;
	}

	private static string BuildIdeaExpansionPrompt(string ideaDescription)
	{
		return $@"You are a software architect helping to expand a brief feature idea into a detailed specification.

## Feature Idea
{ideaDescription}

## Your Task
Expand this idea into a detailed implementation specification. Include:

1. Overview: A clear summary of what the feature does
2. User Stories: Key user interactions (e.g., As a user, I can...)
3. Components: What UI components, services, or data models are needed
4. Implementation Steps: A logical order of implementation tasks
5. Edge Cases: Important scenarios to handle (validation, errors, empty states)
6. Acceptance Criteria: How to verify the feature works correctly

Keep the specification concise but complete. Focus on actionable implementation details.
Do not include code samples - just describe what needs to be built.";
	}
}
