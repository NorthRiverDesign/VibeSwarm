using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Shared.Services;

public partial class IdeaService
{
	public async Task<Job?> ConvertToJobAsync(Guid ideaId, IdeaProcessingOptions? options = null, CancellationToken cancellationToken = default)
	{
		// Per-idea lock: prevents the same idea from being dispatched twice while still
		// allowing different ideas to run through conversion concurrently.
		var ideaLock = _ideaConversionLocks.GetOrAdd(ideaId, _ => new SemaphoreSlim(1, 1));
		await ideaLock.WaitAsync(cancellationToken);
		try
		{
			// Re-fetch the idea inside the lock to ensure we have the latest state
			var idea = await _dbContext.Ideas
				.Include(i => i.Project)
				.Include(i => i.Attachments)
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

			var defaultProvider = await ResolveJobProviderAsync(idea.ProjectId, options, cancellationToken);
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
				? BuildPromptFromExpanded(idea.Description, idea.ExpandedDescription!, await GetApprovedIdeaImplementationPromptTemplateAsync(cancellationToken))
				: BuildImplementationPrompt(idea.Description, await GetIdeaImplementationPromptTemplateAsync(cancellationToken));
			var attachmentPaths = ResolveAttachmentPaths(idea.Project.WorkingPath, idea.Attachments);
			if (attachmentPaths.Count > 0)
			{
				goalPrompt = BuildPromptWithAttachments(goalPrompt, idea.Attachments, attachmentPaths);
			}

			// Create the job with the original idea as the title
			var job = new Job
			{
				ProjectId = idea.ProjectId,
				ProviderId = defaultProvider.Id,
				Title = idea.Description,  // Use the original idea text as the title
				GoalPrompt = goalPrompt,
				Branch = currentBranch,
				ModelUsed = await ResolveJobModelAsync(idea.ProjectId, defaultProvider.Id, options, cancellationToken),
				ReasoningEffort = await ResolveJobReasoningAsync(idea.ProjectId, defaultProvider.Id, cancellationToken),
				AttachedFilesJson = attachmentPaths.Count > 0 ? JsonSerializer.Serialize(attachmentPaths) : null,
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
			ideaLock.Release();
			// Best-effort prune: drop the per-idea semaphore once no one else is waiting
			// so the dictionary doesn't grow unbounded over long-lived app lifetimes.
			if (ideaLock.CurrentCount == 1 && _ideaConversionLocks.TryRemove(ideaId, out var removed) && !ReferenceEquals(removed, ideaLock))
			{
				// A different semaphore was registered for this id since we began; re-register it.
				_ideaConversionLocks.TryAdd(ideaId, removed);
			}
		}
	}

	private async Task<Provider?> ResolveJobProviderAsync(Guid projectId, IdeaProcessingOptions? options, CancellationToken cancellationToken)
	{
		if (options?.ProviderId is Guid providerOverrideId)
		{
			return await _dbContext.Providers
				.AsNoTracking()
				.Where(provider => provider.Id == providerOverrideId && provider.IsEnabled)
				.FirstOrDefaultAsync(cancellationToken);
		}

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

	private async Task<string?> ResolveJobModelAsync(Guid projectId, Guid providerId, IdeaProcessingOptions? options, CancellationToken cancellationToken)
	{
		var modelOverride = string.IsNullOrWhiteSpace(options?.ModelId) ? null : options!.ModelId!.Trim();
		if (modelOverride != null)
		{
			return modelOverride;
		}

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

	private static string BuildPromptFromExpanded(string originalIdea, string expandedDescription, string? template)
		=> PromptBuilder.BuildApprovedIdeaImplementationPrompt(originalIdea, expandedDescription, template);

	private static string BuildImplementationPrompt(string ideaDescription, string? template)
		=> PromptBuilder.BuildIdeaImplementationPrompt(ideaDescription, template);

	private async Task<string?> GetIdeaImplementationPromptTemplateAsync(CancellationToken cancellationToken)
	{
		return await _dbContext.AppSettings
			.AsNoTracking()
			.OrderBy(settings => settings.Id)
			.Select(settings => settings.IdeaImplementationPromptTemplate)
			.FirstOrDefaultAsync(cancellationToken);
	}

	private async Task<string?> GetApprovedIdeaImplementationPromptTemplateAsync(CancellationToken cancellationToken)
	{
		return await _dbContext.AppSettings
			.AsNoTracking()
			.OrderBy(settings => settings.Id)
			.Select(settings => settings.ApprovedIdeaImplementationPromptTemplate)
			.FirstOrDefaultAsync(cancellationToken);
	}

	public async Task<bool> CompleteIdeaFromJobAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas
			.Include(i => i.Project)
			.Include(i => i.Attachments)
			.FirstOrDefaultAsync(i => i.JobId == jobId, cancellationToken);

		if (idea == null)
		{
			return false;
		}

		_logger.LogInformation("Removing completed Idea {IdeaId} after Job {JobId} completed", idea.Id, jobId);
		await DeleteAttachmentFilesAsync(idea.Attachments, idea.Project?.WorkingPath);

		_dbContext.Ideas.Remove(idea);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return true;
	}

	public async Task<bool> HandleJobCompletionAsync(Guid jobId, bool success, CancellationToken cancellationToken = default)
	{
		var idea = await _dbContext.Ideas
			.Include(i => i.Project)
			.Include(i => i.Attachments)
			.FirstOrDefaultAsync(i => i.JobId == jobId, cancellationToken);

		if (idea == null)
		{
			return false;
		}

		var stoppedProcessing = false;

		if (success)
		{
			var shouldStopProcessing = idea.Project?.IdeasProcessingActive == true
				&& !await _dbContext.Ideas
					.AsNoTracking()
					.AnyAsync(otherIdea => otherIdea.ProjectId == idea.ProjectId && otherIdea.Id != idea.Id, cancellationToken);

			// Job completed successfully - remove the idea
			_logger.LogInformation("Removing completed Idea {IdeaId} after Job {JobId} completed successfully", idea.Id, jobId);
			await DeleteAttachmentFilesAsync(idea.Attachments, idea.Project?.WorkingPath);
			_dbContext.Ideas.Remove(idea);

			if (shouldStopProcessing)
			{
				var project = await _dbContext.Projects.FindAsync(new object[] { idea.ProjectId }, cancellationToken);
				if (project?.IdeasProcessingActive == true)
				{
					project.IdeasProcessingActive = false;
					project.IdeasProcessingProviderId = null;
					project.IdeasProcessingModelId = null;
					stoppedProcessing = true;
					_logger.LogInformation(
						"Stopped Ideas auto-processing for project {ProjectId} because Job {JobId} completed the last queued idea",
						idea.ProjectId,
						jobId);
				}
			}
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

	private static List<string> ResolveAttachmentPaths(string? workingPath, IEnumerable<IdeaAttachment>? attachments)
	{
		var resolved = new List<string>();
		if (string.IsNullOrWhiteSpace(workingPath) || attachments == null)
		{
			return resolved;
		}

		var normalizedRoot = Path.GetFullPath(workingPath);
		foreach (var attachment in attachments)
		{
			if (string.IsNullOrWhiteSpace(attachment.RelativePath))
			{
				continue;
			}

			var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, attachment.RelativePath));
			if (!fullPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
			{
				continue;
			}

			if (File.Exists(fullPath))
			{
				resolved.Add(fullPath);
			}
		}

		return resolved;
	}

	private static string BuildPromptWithAttachments(string prompt, IEnumerable<IdeaAttachment> attachments, IReadOnlyList<string> attachmentPaths)
	{
		var attachmentList = attachments.ToList();
		if (attachmentList.Count == 0 || attachmentPaths.Count == 0)
		{
			return prompt;
		}

		var sb = new StringBuilder();
		sb.AppendLine(prompt.TrimEnd());
		sb.AppendLine();
		sb.AppendLine("## Attached Context Files");
		sb.AppendLine("The user attached the following files as additional context. Review them before implementing the change.");
		sb.AppendLine();

		for (var index = 0; index < attachmentList.Count && index < attachmentPaths.Count; index++)
		{
			var attachment = attachmentList[index];
			sb.Append("- `");
			sb.Append(attachment.FileName);
			sb.Append("`");
			if (!string.IsNullOrWhiteSpace(attachment.ContentType))
			{
				sb.Append(" (");
				sb.Append(attachment.ContentType);
				sb.Append(')');
			}
			sb.Append(" - path: `");
			sb.Append(attachmentPaths[index]);
			sb.AppendLine("`");
		}

		return sb.ToString();
	}
}
