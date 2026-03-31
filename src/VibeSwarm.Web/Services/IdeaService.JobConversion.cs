using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Shared.Services;

public partial class IdeaService
{
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

			var defaultProvider = await ResolveJobProviderAsync(idea.ProjectId, cancellationToken);
			if (defaultProvider == null)
			{
				_logger.LogWarning("No default provider configured. Cannot convert idea to job.");
				// Reset processing state on failure
				idea.IsProcessing = false;
				await _dbContext.SaveChangesAsync(cancellationToken);
				return null;
			}

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

			// Use an approved expansion when available; otherwise send directly to implementation.
			var goalPrompt = idea.HasExpandedDescription
				? BuildPromptFromExpanded(idea.Description, idea.ExpandedDescription!)
				: BuildImplementationPrompt(idea.Description);

			// Create the job with the original idea as the title
			var job = new Job
			{
				ProjectId = idea.ProjectId,
				ProviderId = defaultProvider.Id,
				Title = idea.Description,  // Use the original idea text as the title
				GoalPrompt = goalPrompt,
				Branch = currentBranch,
				ModelUsed = await ResolveJobModelAsync(idea.ProjectId, defaultProvider.Id, cancellationToken),
				ReasoningEffort = await ResolveJobReasoningAsync(idea.ProjectId, defaultProvider.Id, cancellationToken),
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

	private async Task<Provider?> ResolveJobProviderAsync(Guid projectId, CancellationToken cancellationToken)
	{
		var projectProvider = await _dbContext.ProjectProviders
			.AsNoTracking()
			.Where(selection => selection.ProjectId == projectId && selection.IsEnabled)
			.OrderBy(selection => selection.Priority)
			.Join(
				_dbContext.Providers.AsNoTracking().Where(provider => provider.IsEnabled),
				selection => selection.ProviderId,
				provider => provider.Id,
				(_, provider) => provider)
			.FirstOrDefaultAsync(cancellationToken);

		return projectProvider ?? await _providerService.GetDefaultAsync(cancellationToken);
	}

	private async Task<string?> ResolveJobModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken)
	{
		var projectSelection = await _dbContext.ProjectProviders
			.AsNoTracking()
			.Where(selection => selection.ProjectId == projectId && selection.IsEnabled && selection.ProviderId == providerId)
			.OrderBy(selection => selection.Priority)
			.Select(selection => selection.PreferredModelId)
			.FirstOrDefaultAsync(cancellationToken);

		return string.IsNullOrWhiteSpace(projectSelection) ? null : projectSelection.Trim();
	}

	private async Task<string?> ResolveJobReasoningAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken)
	{
		var projectSelection = await _dbContext.ProjectProviders
			.AsNoTracking()
			.Where(selection => selection.ProjectId == projectId && selection.IsEnabled && selection.ProviderId == providerId)
			.OrderBy(selection => selection.Priority)
			.Select(selection => selection.PreferredReasoningEffort)
			.FirstOrDefaultAsync(cancellationToken);

		if (!string.IsNullOrWhiteSpace(projectSelection))
		{
			return ProviderCapabilities.NormalizeReasoningEffort(projectSelection);
		}

		var provider = await _dbContext.Providers
			.AsNoTracking()
			.Where(item => item.Id == providerId)
			.Select(item => new { item.DefaultReasoningEffort })
			.FirstOrDefaultAsync(cancellationToken);

		return ProviderCapabilities.NormalizeReasoningEffort(provider?.DefaultReasoningEffort);
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
5. Use subagents for research, codebase exploration, and parallel analysis to keep your context window efficient

Implement this feature now.

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
6. Use subagents for research, codebase exploration, and parallel analysis to keep your context window efficient

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

		var stoppedProcessing = false;

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

			// If the job was cancelled, stop ideas auto-processing to avoid repeat failures
			// (e.g. provider maintenance or rate limits causing the user to cancel)
			var job = await _dbContext.Jobs
				.AsNoTracking()
				.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

			if (job?.Status == JobStatus.Cancelled)
			{
				var project = await _dbContext.Projects.FindAsync(new object[] { idea.ProjectId }, cancellationToken);
				if (project?.IdeasProcessingActive == true)
				{
					project.IdeasProcessingActive = false;
					stoppedProcessing = true;
					_logger.LogInformation("Stopped Ideas auto-processing for project {ProjectId} because Job {JobId} was cancelled", idea.ProjectId, jobId);
				}
			}
		}

		await _dbContext.SaveChangesAsync(cancellationToken);

		// Notify clients about the idea state change
		if (_jobUpdateService != null)
		{
			try
			{
				await _jobUpdateService.NotifyIdeaUpdated(idea.Id, idea.ProjectId);

				if (stoppedProcessing)
				{
					await _jobUpdateService.NotifyIdeasProcessingStateChanged(idea.ProjectId, false);
				}
			}
			catch { /* Don't fail if notification fails */ }
		}

		return true;
	}
}
