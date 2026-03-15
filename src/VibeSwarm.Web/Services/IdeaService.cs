using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.LocalInference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

public class IdeaService : IIdeaService
{
	private const int DefaultIdeaPageSize = 10;
	private const int MaxPageSize = 100;

	private readonly VibeSwarmDbContext _dbContext;
	private readonly IJobService _jobService;
	private readonly IProviderService _providerService;
	private readonly IVersionControlService _versionControlService;
	private readonly IInferenceService? _inferenceService;
	private readonly IJobUpdateService? _jobUpdateService;
	private readonly ILogger<IdeaService> _logger;

	/// <summary>
	/// Timeout for AI expansion operations (5 minutes)
	/// </summary>
	private static readonly TimeSpan ExpansionTimeout = TimeSpan.FromMinutes(5);

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
		IInferenceService? inferenceService = null,
		IJobUpdateService? jobUpdateService = null)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_providerService = providerService;
		_versionControlService = versionControlService;
		_logger = logger;
		_inferenceService = inferenceService;
		_jobUpdateService = jobUpdateService;
	}

	public async Task<IEnumerable<Idea>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Include(i => i.Job)
			.Where(i => i.ProjectId == projectId)
			.OrderBy(i => i.SortOrder)
			.ThenBy(i => i.CreatedAt)
			.ToListAsync(cancellationToken);
	}

	public async Task<ProjectIdeasListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = DefaultIdeaPageSize, CancellationToken cancellationToken = default)
	{
		var normalizedPageSize = NormalizePageSize(pageSize, DefaultIdeaPageSize);
		var baseQuery = _dbContext.Ideas
			.Where(i => i.ProjectId == projectId);

		var totalCount = await baseQuery.CountAsync(cancellationToken);
		var normalizedPage = NormalizePageNumber(page, normalizedPageSize, totalCount);

		return new ProjectIdeasListResult
		{
			PageNumber = normalizedPage,
			PageSize = normalizedPageSize,
			TotalCount = totalCount,
			UnprocessedCount = await baseQuery.CountAsync(i => !i.IsProcessing && i.JobId == null, cancellationToken),
			Items = await baseQuery
				.Include(i => i.Job)
				.OrderBy(i => i.SortOrder)
				.ThenBy(i => i.CreatedAt)
				.Skip((normalizedPage - 1) * normalizedPageSize)
				.Take(normalizedPageSize)
				.ToListAsync(cancellationToken)
		};
	}

	public async Task<Idea?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.Include(i => i.Project)
			.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
	}

	public async Task<Idea> CreateAsync(Idea idea, CancellationToken cancellationToken = default)
	{
		// Check for duplicate: same project + description within the last 10 seconds
		var duplicateCutoff = DateTime.UtcNow.AddSeconds(-10);
		var existingDuplicate = await _dbContext.Ideas
			.FirstOrDefaultAsync(i => i.ProjectId == idea.ProjectId
				&& i.Description == idea.Description
				&& i.CreatedAt >= duplicateCutoff, cancellationToken);

		if (existingDuplicate != null)
		{
			_logger.LogWarning("Duplicate idea rejected for project {ProjectId}: \"{Description}\" (existing idea {IdeaId} created at {CreatedAt})",
				idea.ProjectId, idea.Description?.Length > 80 ? idea.Description[..80] + "..." : idea.Description,
				existingDuplicate.Id, existingDuplicate.CreatedAt);
			return existingDuplicate;
		}

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

			// Use an approved expansion when available; otherwise honor the project's auto-expand setting.
			var goalPrompt = idea.HasExpandedDescription
				? BuildPromptFromExpanded(idea.Description, idea.ExpandedDescription!)
				: idea.Project.IdeasAutoExpand
					? BuildExpandedPrompt(idea.Description)
					: BuildImplementationPrompt(idea.Description);

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

Implement this feature now.

When you are finished, end your response with a short summary in this exact format:
<commit-summary>
A concise one-line description of what was implemented (max 72 chars)
</commit-summary>";
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

Begin by expanding this idea into a detailed specification, then implement it.

When you are finished, end your response with a short summary in this exact format:
<commit-summary>
A concise one-line description of what was implemented (max 72 chars)
</commit-summary>";
	}

	private static string BuildImplementationPrompt(string ideaDescription)
	{
		return $@"You are implementing a feature based on the following idea. Work directly from the idea below instead of first expanding it into a separate detailed specification.

## Feature Idea
{ideaDescription}

## Instructions
1. Implement the feature directly from the idea above
2. Fill in necessary implementation details while staying aligned with the original intent
3. Consider edge cases and error handling
4. Make sure the implementation follows the existing code patterns and style in the project
5. Add or update tests when needed to cover the change

Begin implementing this feature now without first expanding it into a detailed specification.

When you are finished, end your response with a short summary in this exact format:
<commit-summary>
A concise one-line description of what was implemented (max 72 chars)
</commit-summary>";
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

	public async Task<Idea?> ExpandIdeaAsync(Guid ideaId, IdeaExpansionRequest? request = null, CancellationToken cancellationToken = default)
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
		await NotifyIdeaUpdateSafe(idea.Id, idea.ProjectId);

		// Create a linked cancellation token with timeout to prevent hanging
		using var timeoutCts = new CancellationTokenSource(ExpansionTimeout);
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
		var expandToken = linkedCts.Token;

		try
		{
			var useLocalInference = request?.UseLocalInference ?? false;
			var (providerId, modelName, usePlanningMode) = ResolveProviderExpansionRequest(idea.Project, request);

			if (useLocalInference)
			{
				await ExpandWithLocalInferenceAsync(idea, modelName, expandToken);
			}
			else
			{
				await ExpandWithProviderAsync(idea, providerId, modelName, usePlanningMode, expandToken);
			}
		}
		catch (OperationCanceledException)
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = cancellationToken.IsCancellationRequested
				? "Expansion was cancelled"
				: "Expansion timed out after " + (int)ExpansionTimeout.TotalMinutes + " minutes";
			_logger.LogWarning("Idea {IdeaId} expansion cancelled/timed out", ideaId);
		}
		catch (Exception ex)
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = ex.Message;
			_logger.LogError(ex, "Error expanding idea {IdeaId}", ideaId);
		}

		// Always save final state and notify, even if cancelled
		try
		{
			await _dbContext.SaveChangesAsync(CancellationToken.None);
			await NotifyIdeaUpdateSafe(idea.Id, idea.ProjectId);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to save final expansion state for idea {IdeaId}", ideaId);
		}

		return idea;
	}

	/// <summary>
	/// Expands an idea using a CLI coding provider (Claude, Copilot, OpenCode)
	/// </summary>
	private async Task ExpandWithProviderAsync(
		Idea idea,
		Guid? providerId,
		string? modelName,
		bool usePlanningMode,
		CancellationToken cancellationToken)
	{
		Provider? provider;
		if (providerId.HasValue)
		{
			provider = await _providerService.GetByIdAsync(providerId.Value, cancellationToken);
			if (provider == null)
			{
				idea.ExpansionStatus = IdeaExpansionStatus.Failed;
				idea.ExpansionError = "Selected provider not found";
				return;
			}
		}
		else
		{
			provider = await _providerService.GetDefaultAsync(cancellationToken);
		}

		if (provider == null)
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = "No default provider configured";
			return;
		}

		if (!provider.IsEnabled)
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = usePlanningMode
				? "The selected planning provider is disabled"
				: "The selected provider is disabled";
			return;
		}

		if (usePlanningMode && !SupportsPlanningMode(provider.Type))
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = "Planning currently supports only Claude and GitHub Copilot providers";
			return;
		}

		var providerInstance = _providerService.CreateInstance(provider);
		if (providerInstance == null)
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = "Could not create provider instance";
			return;
		}

		var expansionPrompt = BuildIdeaExpansionPrompt(idea.Description);
		var response = await ExecuteProviderExpansionAsync(
			providerInstance,
			provider,
			idea.Project!.WorkingPath,
			idea.Description,
			expansionPrompt,
			modelName,
			usePlanningMode,
			cancellationToken);

		if (response.Success && !string.IsNullOrWhiteSpace(response.Response))
		{
			idea.ExpandedDescription = response.Response.Trim();
			idea.ExpansionStatus = IdeaExpansionStatus.PendingReview;
			idea.ExpandedAt = DateTime.UtcNow;
			_logger.LogInformation("Successfully expanded idea {IdeaId} using CLI provider", idea.Id);
		}
		else
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = response.ErrorMessage ?? "No response from provider";
			_logger.LogWarning("Failed to expand idea {IdeaId}: {Error}", idea.Id, idea.ExpansionError);
		}
	}

	private async Task<PromptResponse> ExecuteProviderExpansionAsync(
		IProvider providerInstance,
		Provider provider,
		string workingDirectory,
		string ideaDescription,
		string expansionPrompt,
		string? modelName,
		bool usePlanningMode,
		CancellationToken cancellationToken)
	{
		if (!usePlanningMode && string.IsNullOrWhiteSpace(modelName))
		{
			return await providerInstance.GetPromptResponseAsync(expansionPrompt, workingDirectory, cancellationToken);
		}

		var result = await providerInstance.ExecuteWithOptionsAsync(
			usePlanningMode ? BuildProviderPlanningPrompt(ideaDescription) : expansionPrompt,
			new ExecutionOptions
			{
				WorkingDirectory = workingDirectory,
				Model = modelName
			},
			cancellationToken: cancellationToken);
		var responseText = ExtractExecutionText(result);
		if (result.Success && !string.IsNullOrWhiteSpace(responseText))
		{
			return PromptResponse.Ok(responseText.Trim(), model: result.ModelUsed ?? modelName);
		}

		var errorMessage = result.ErrorMessage;
		if (string.IsNullOrWhiteSpace(errorMessage))
		{
			errorMessage = usePlanningMode
				? $"{provider.Name} did not return a plan"
				: $"{provider.Name} did not return a response";
		}

		return PromptResponse.Fail(errorMessage);
	}

	private static (Guid? ProviderId, string? ModelName, bool UsePlanningMode) ResolveProviderExpansionRequest(Project project, IdeaExpansionRequest? request)
	{
		var requestedModelName = string.IsNullOrWhiteSpace(request?.ModelName)
			? null
			: request.ModelName.Trim();
		if (request?.UseLocalInference == true)
		{
			return (request.ProviderId, requestedModelName, false);
		}

		if (!project.PlanningEnabled)
		{
			return (request?.ProviderId, requestedModelName, false);
		}

		if (request?.ProviderId.HasValue == true || requestedModelName != null)
		{
			var usePlanningMode = project.PlanningProviderId.HasValue &&
				request?.ProviderId == project.PlanningProviderId &&
				(requestedModelName == null || string.Equals(requestedModelName, project.PlanningModelId, StringComparison.Ordinal));
			return (request?.ProviderId, requestedModelName, usePlanningMode);
		}

		return (project.PlanningProviderId, project.PlanningModelId, project.PlanningProviderId.HasValue);
	}

	private static bool SupportsPlanningMode(ProviderType providerType)
	{
		return providerType is ProviderType.Claude or ProviderType.Copilot;
	}

	private static string BuildProviderPlanningPrompt(string ideaDescription)
	{
		return $@"/plan Expand the feature idea below into a detailed, implementation-ready specification that can be reviewed before coding.

## Feature Idea
{ideaDescription}

## Planning Requirements
1. Overview: Summarize the feature and intended outcome
2. User Experience: Describe the primary user interactions and flows
3. Components: Identify the pages, components, services, data models, or infrastructure involved
4. Implementation Steps: Outline a sensible build order
5. Edge Cases: Call out important validation, error, and empty-state scenarios
6. Acceptance Criteria: List the observable behaviors that confirm completion

Return only the plan/specification. Do not implement the feature and do not include code samples.";
	}

	private static string? ExtractExecutionText(ExecutionResult result)
	{
		var preferredRoles = new[] { "plan", "assistant", "response", "message", "suggestion" };
		foreach (var role in preferredRoles)
		{
			var content = result.Messages
				.Where(message =>
					string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase) &&
					!string.IsNullOrWhiteSpace(message.Content))
				.Select(message => message.Content.Trim())
				.ToList();
			if (content.Count > 0)
			{
				return string.Join(Environment.NewLine + Environment.NewLine, content);
			}
		}

		return string.IsNullOrWhiteSpace(result.Output)
			? null
			: result.Output.Trim();
	}

	/// <summary>
	/// Expands an idea using a local inference provider (e.g., Ollama)
	/// </summary>
	private async Task ExpandWithLocalInferenceAsync(Idea idea, string? modelName, CancellationToken cancellationToken)
	{
		if (_inferenceService == null)
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = "Local inference service is not available";
			return;
		}

		var expansionPrompt = BuildIdeaExpansionPrompt(idea.Description);

		var inferenceRequest = new InferenceRequest
		{
			Prompt = expansionPrompt,
			SystemPrompt = "You are a software architect helping to expand feature ideas into detailed specifications.",
			TaskType = "idea_expansion",
			Model = modelName
		};

		var response = await _inferenceService.GenerateAsync(inferenceRequest, cancellationToken);

		if (response.Success && !string.IsNullOrWhiteSpace(response.Response))
		{
			idea.ExpandedDescription = response.Response.Trim();
			idea.ExpansionStatus = IdeaExpansionStatus.PendingReview;
			idea.ExpandedAt = DateTime.UtcNow;
			_logger.LogInformation("Successfully expanded idea {IdeaId} using local inference (model: {Model})",
				idea.Id, response.ModelUsed ?? modelName ?? "default");
		}
		else
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = response.Error ?? "No response from local inference";
			_logger.LogWarning("Failed to expand idea {IdeaId} with local inference: {Error}", idea.Id, idea.ExpansionError);
		}
	}

	public async Task<Idea?> CancelExpansionAsync(Guid ideaId, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas.FindAsync(new object[] { ideaId }, cancellationToken);
		if (idea == null)
		{
			return null;
		}

		// Only cancel if currently expanding
		if (idea.ExpansionStatus != IdeaExpansionStatus.Expanding)
		{
			return idea;
		}

		idea.ExpansionStatus = IdeaExpansionStatus.NotExpanded;
		idea.ExpansionError = null;
		idea.ExpandedDescription = null;
		idea.ExpandedAt = null;
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Cancelled expansion for idea {IdeaId}", ideaId);
		await NotifyIdeaUpdateSafe(ideaId, idea.ProjectId);

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

	public async Task<SuggestIdeasResult> SuggestIdeasFromCodebaseAsync(Guid projectId, SuggestIdeasRequest? request = null, CancellationToken cancellationToken = default)
	{
		var normalizedRequest = NormalizeSuggestIdeasRequest(request);

		if (_inferenceService == null)
		{
			_logger.LogWarning("Local inference service is not configured for project {ProjectId}", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.NotConfigured,
				Message = "No local inference service is configured. Add a provider under Settings → Local Inference."
			};
		}

		try
		{
			return await SuggestIdeasInternalAsync(projectId, normalizedRequest, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Codebase suggestion was cancelled for project {ProjectId}", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = "The suggestion request was cancelled."
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unhandled error in SuggestIdeasFromCodebaseAsync for project {ProjectId}", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = $"An unexpected error occurred: {ex.Message}",
				InferenceError = ex.ToString()
			};
		}
	}

	/// <summary>
	/// Internal implementation of SuggestIdeasFromCodebaseAsync with all per-stage error handling.
	/// The public method wraps this with a top-level safety net.
	/// </summary>
	private async Task<SuggestIdeasResult> SuggestIdeasInternalAsync(Guid projectId, SuggestIdeasRequest request, CancellationToken cancellationToken)
	{
		var project = await _dbContext.Projects
			.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

		if (project == null || string.IsNullOrEmpty(project.WorkingPath))
		{
			_logger.LogWarning("Project {ProjectId} not found or has no working path", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.RepoMapFailed,
				Message = "Project not found or has no working directory configured."
			};
		}

		InferenceProvider? selectedProvider = null;
		if (request.ProviderId.HasValue)
		{
			selectedProvider = await _dbContext.InferenceProviders
				.Include(provider => provider.Models)
				.FirstOrDefaultAsync(provider => provider.Id == request.ProviderId.Value && provider.IsEnabled, cancellationToken);

			if (selectedProvider == null)
			{
				_logger.LogWarning("Requested inference provider {ProviderId} was not found for project {ProjectId}", request.ProviderId.Value, projectId);
				return new SuggestIdeasResult
				{
					Stage = SuggestIdeasStage.ProviderNotFound,
					Message = "The selected inference provider is no longer available. Choose another provider and try again."
				};
			}
		}

		var providerDisplayName = selectedProvider?.Name ?? "Local inference provider";
		var providerEndpoint = selectedProvider?.Endpoint;

		// Verify the provider is reachable and discover any assigned model
		InferenceHealthResult health;
		try
		{
			// _inferenceService is guaranteed non-null by the caller (SuggestIdeasFromCodebaseAsync checks it)
			health = await _inferenceService!.CheckHealthAsync(providerEndpoint, cancellationToken);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Health check failed for project {ProjectId} suggestion using provider {Provider}", projectId, providerDisplayName);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ProviderUnreachable,
				Message = $"Could not reach {providerDisplayName}: {ex.Message}",
				InferenceError = ex.Message
			};
		}

		if (!health.IsAvailable)
		{
			var detail = string.IsNullOrEmpty(health.Error) ? "No error detail returned." : health.Error;
			_logger.LogWarning("{Provider} unavailable for project {ProjectId}: {Error}", providerDisplayName, projectId, detail);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ProviderUnreachable,
				Message = $"{providerDisplayName} is not responding. {detail}",
				InferenceError = detail
			};
		}

		// Build the repo map — gives the model a compact view of the codebase
		var repoMap = RepoMapGenerator.GenerateRepoMap(project.WorkingPath);
		if (string.IsNullOrEmpty(repoMap))
		{
			_logger.LogWarning("Repo map generation failed for project {ProjectId} at path {Path}", projectId, project.WorkingPath);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.RepoMapFailed,
				Message = $"Could not scan the project directory at \"{project.WorkingPath}\". Verify the path exists and is readable."
			};
		}

		var prompt = BuildCodebaseSuggestionPrompt(repoMap, project.Name, project.Description, project.PromptContext, request.IdeaCount);
		const string systemPrompt = "You are a senior software engineer performing a codebase review. Identify concrete, actionable improvements. Return only a plain list of ideas, one per line starting with \"- \". No explanations or headers.";

		InferenceResponse inferenceResponse;
		try
		{
			_logger.LogInformation(
				"Sending codebase suggestion request to local inference for project {ProjectId} using provider {Provider} requesting {Count} ideas",
				projectId,
				providerDisplayName,
				request.IdeaCount);

			if (selectedProvider != null)
			{
				var selectedModel = ResolveSuggestionModel(selectedProvider);
				if (selectedModel == null)
				{
					return new SuggestIdeasResult
					{
						Stage = SuggestIdeasStage.NoModel,
						Message = $"No model is assigned to the \"suggest\" or \"default\" task for {selectedProvider.Name}. Assign one under Settings → Local Inference."
					};
				}

				inferenceResponse = await _inferenceService!.GenerateAsync(new InferenceRequest
				{
					TaskType = "suggest",
					Prompt = prompt,
					SystemPrompt = systemPrompt,
					Endpoint = selectedProvider.Endpoint,
					Model = selectedModel.ModelId
				}, cancellationToken);
			}
			else
			{
				inferenceResponse = await _inferenceService!.GenerateForTaskAsync("suggest", prompt, systemPrompt, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Codebase suggestion request timed out for project {ProjectId}", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = "The inference request timed out. Try a smaller or faster model, or increase the client timeout."
			};
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Inference request failed for project {ProjectId} suggestion", projectId);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = $"Inference request failed: {ex.Message}",
				InferenceError = ex.Message
			};
		}

		if (!inferenceResponse.Success || string.IsNullOrWhiteSpace(inferenceResponse.Response))
		{
			var error = inferenceResponse.Error ?? "The model returned an empty response.";
			_logger.LogWarning("Inference returned no usable response for project {ProjectId}: {Error}", projectId, error);

			// Distinguish between "no model configured" and a genuine generation failure
			var isNoModel = error.Contains("No model configured", StringComparison.OrdinalIgnoreCase)
				|| error.Contains("model", StringComparison.OrdinalIgnoreCase) && error.Contains("configure", StringComparison.OrdinalIgnoreCase);

			return new SuggestIdeasResult
			{
				Stage = isNoModel ? SuggestIdeasStage.NoModel : SuggestIdeasStage.GenerateFailed,
				Message = isNoModel
					? "No model is assigned to the \"suggest\" or \"default\" task. Go to Settings → Local Inference to assign a model."
					: $"The model did not return a usable response: {error}",
				ModelUsed = inferenceResponse.ModelUsed,
				InferenceDurationMs = inferenceResponse.DurationMs,
				InferenceError = error
			};
		}

		var suggestions = ParseCodebaseSuggestions(inferenceResponse.Response, request.IdeaCount);
		if (suggestions.Count == 0)
		{
			_logger.LogWarning("No parseable suggestions in inference response for project {ProjectId}. Raw response length: {Len}",
				projectId, inferenceResponse.Response.Length);
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.ParseFailed,
				Message = "The model responded but did not produce ideas in the expected format. Try a different model or re-run.",
				ModelUsed = inferenceResponse.ModelUsed,
				InferenceDurationMs = inferenceResponse.DurationMs,
				InferenceError = $"Raw response ({inferenceResponse.Response.Length} chars): {inferenceResponse.Response[..Math.Min(200, inferenceResponse.Response.Length)]}…"
			};
		}

		var createdIdeas = new List<Idea>();
		foreach (var suggestion in suggestions)
		{
			try
			{
				var idea = await CreateAsync(new Idea
				{
					ProjectId = projectId,
					Description = suggestion
				}, cancellationToken);
				createdIdeas.Add(idea);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to save suggested idea for project {ProjectId}", projectId);
			}
		}

		if (createdIdeas.Count == 0)
		{
			return new SuggestIdeasResult
			{
				Stage = SuggestIdeasStage.GenerateFailed,
				Message = "Ideas were generated, but none could be saved. Check the logs and try again.",
				ModelUsed = inferenceResponse.ModelUsed,
				InferenceDurationMs = inferenceResponse.DurationMs
			};
		}

		_logger.LogInformation("Created {Count} suggested ideas for project {ProjectId} using model {Model}",
			createdIdeas.Count, projectId, inferenceResponse.ModelUsed ?? "unknown");

		var message = createdIdeas.Count == request.IdeaCount
			? $"{createdIdeas.Count} idea{(createdIdeas.Count == 1 ? "" : "s")} added from codebase analysis."
			: $"{createdIdeas.Count} of {request.IdeaCount} requested idea{(request.IdeaCount == 1 ? "" : "s")} added from codebase analysis.";

		return new SuggestIdeasResult
		{
			Success = true,
			Stage = SuggestIdeasStage.Success,
			Ideas = createdIdeas,
			Message = message,
			ModelUsed = inferenceResponse.ModelUsed,
			InferenceDurationMs = inferenceResponse.DurationMs
		};
	}

	private static string BuildCodebaseSuggestionPrompt(string repoMap, string projectName, string? description, string? promptContext, int ideaCount)
	{
		var sb = new StringBuilder();

		sb.AppendLine($"<overview>");
		sb.AppendLine($"You are reviewing the \"{projectName}\" codebase to identify improvements and missing features.");
		sb.AppendLine("</overview>");

		if (!string.IsNullOrEmpty(description))
		{
			sb.AppendLine("<context>");
			sb.AppendLine($"Project description: {description}");
			sb.AppendLine("</context>");
		}

		if (!string.IsNullOrEmpty(promptContext))
		{
			sb.AppendLine("<project_instructions>");
			sb.AppendLine(promptContext);
			sb.AppendLine("</project_instructions>");
		}

		sb.AppendLine("<repository_structure>");
		sb.AppendLine(repoMap);
		sb.AppendLine("</repository_structure>");

		sb.AppendLine("<objective>");
		sb.AppendLine("Analyze the repository structure and identify areas for improvement. Consider:");
		sb.AppendLine("- Missing features that would benefit users");
		sb.AppendLine("- Error handling, logging, or validation gaps");
		sb.AppendLine("- Performance or security improvements");
		sb.AppendLine("- Testing gaps or developer experience improvements");
		sb.AppendLine("- UX improvements for any UI components");
		sb.AppendLine("</objective>");

		sb.AppendLine("<goal>");
		sb.AppendLine($"Return exactly {ideaCount} concrete, actionable idea{(ideaCount == 1 ? "" : "s")}. Each idea must be a short description (1-2 sentences).");
		sb.AppendLine("Format: one idea per line, each starting with \"- \". No headers, no explanations outside the list.");
		sb.AppendLine("Example:");
		sb.AppendLine("- Add input validation to the registration form to prevent invalid email addresses");
		sb.AppendLine("- Implement a retry mechanism for failed API calls to improve reliability");
		sb.AppendLine("</goal>");

		return sb.ToString();
	}

	/// <summary>
	/// Parses a list of suggestions from an LLM response.
	/// Handles common formats: "- item", "• item", and "1. item".
	/// </summary>
	private static List<string> ParseCodebaseSuggestions(string response, int maxSuggestions)
	{
		var suggestions = new List<string>();

		foreach (var rawLine in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var line = rawLine.Trim();

			string? extracted = null;

			if (line.StartsWith("- ") && line.Length > 2)
				extracted = line[2..].Trim();
			else if (line.StartsWith("• ") && line.Length > 2)
				extracted = line[2..].Trim();
			else if (line.Length > 3 && char.IsDigit(line[0]) && line[1] == '.' && line[2] == ' ')
				extracted = line[3..].Trim();
			else if (line.Length > 4 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && line[2] == '.' && line[3] == ' ')
				extracted = line[4..].Trim();

			if (!string.IsNullOrWhiteSpace(extracted) && extracted.Length >= 10)
				suggestions.Add(extracted);
		}

		return suggestions
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(maxSuggestions)
			.ToList();
	}

	private static SuggestIdeasRequest NormalizeSuggestIdeasRequest(SuggestIdeasRequest? request)
	{
		return new SuggestIdeasRequest
		{
			ProviderId = request?.ProviderId,
			IdeaCount = Math.Clamp(request?.IdeaCount ?? SuggestIdeasRequest.DefaultIdeaCount, SuggestIdeasRequest.MinIdeaCount, SuggestIdeasRequest.MaxIdeaCount)
		};
	}

	private static InferenceModel? ResolveSuggestionModel(InferenceProvider provider)
	{
		return provider.Models
			.Where(model => model.IsAvailable && model.IsDefault && string.Equals(model.TaskType, "suggest", StringComparison.OrdinalIgnoreCase))
			.OrderBy(model => model.ModelId)
			.FirstOrDefault()
			?? provider.Models
				.Where(model => model.IsAvailable && model.IsDefault && string.Equals(model.TaskType, "default", StringComparison.OrdinalIgnoreCase))
				.OrderBy(model => model.ModelId)
				.FirstOrDefault();
	}

	private static int NormalizePageSize(int pageSize, int defaultPageSize)
	{
		if (pageSize <= 0)
		{
			return defaultPageSize;
		}

		return Math.Min(pageSize, MaxPageSize);
	}

	private static int NormalizePageNumber(int pageNumber, int pageSize, int totalCount)
	{
		if (totalCount <= 0)
		{
			return 1;
		}

		var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
		return Math.Min(Math.Max(pageNumber, 1), totalPages);
	}
}
