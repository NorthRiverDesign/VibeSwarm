using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Shared.Services;

public class IdeaService : IIdeaService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IJobService _jobService;
	private readonly IProviderService _providerService;
	private readonly IVersionControlService _versionControlService;
	private readonly ILogger<IdeaService> _logger;

	public IdeaService(
		VibeSwarmDbContext dbContext,
		IJobService jobService,
		IProviderService providerService,
		IVersionControlService versionControlService,
		ILogger<IdeaService> logger)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_providerService = providerService;
		_versionControlService = versionControlService;
		_logger = logger;
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
		return existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas.FindAsync(new object[] { id }, cancellationToken);
		if (idea != null)
		{
			_dbContext.Ideas.Remove(idea);
			await _dbContext.SaveChangesAsync(cancellationToken);
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
		var idea = await _dbContext.Ideas
			.Include(i => i.Project)
			.FirstOrDefaultAsync(i => i.Id == ideaId, cancellationToken);

		if (idea?.Project == null)
		{
			_logger.LogWarning("Idea {IdeaId} not found or has no project", ideaId);
			return null;
		}

		// Get the default provider
		var defaultProvider = await _providerService.GetDefaultAsync(cancellationToken);
		if (defaultProvider == null)
		{
			_logger.LogWarning("No default provider configured. Cannot convert idea to job.");
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

		// Build the prompt that expands the idea into a feature spec
		var expandedPrompt = BuildExpandedPrompt(idea.Description);

		// Create the job
		var job = new Job
		{
			ProjectId = idea.ProjectId,
			ProviderId = defaultProvider.Id,
			GoalPrompt = expandedPrompt,
			ModelUsed = defaultModel,
			Branch = currentBranch,
			Status = JobStatus.New
		};

		var createdJob = await _jobService.CreateAsync(job, cancellationToken);

		// Link the idea to the job and mark it as processing
		idea.JobId = createdJob.Id;
		idea.IsProcessing = true;
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Converted Idea {IdeaId} to Job {JobId}", ideaId, createdJob.Id);

		return createdJob;
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

	public async Task<Idea?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Ideas
			.FirstOrDefaultAsync(i => i.JobId == jobId, cancellationToken);
	}

	public async Task StartProcessingAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var project = await _dbContext.Projects.FindAsync(new object[] { projectId }, cancellationToken);
		if (project != null)
		{
			project.IdeasProcessingActive = true;
			await _dbContext.SaveChangesAsync(cancellationToken);
			_logger.LogInformation("Started Ideas auto-processing for project {ProjectId}", projectId);
		}
	}

	public async Task StopProcessingAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var project = await _dbContext.Projects.FindAsync(new object[] { projectId }, cancellationToken);
		if (project != null)
		{
			project.IdeasProcessingActive = false;
			await _dbContext.SaveChangesAsync(cancellationToken);
			_logger.LogInformation("Stopped Ideas auto-processing for project {ProjectId}", projectId);
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
}
