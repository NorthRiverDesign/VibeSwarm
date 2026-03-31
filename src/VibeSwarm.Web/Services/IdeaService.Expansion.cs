using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Services;

public partial class IdeaService
{
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
			var useInference = request?.UseInference ?? false;
			var (providerId, modelName, usePlanningMode) = ResolveProviderExpansionRequest(idea.Project, request);

			if (useInference)
			{
				await ExpandWithInferenceAsync(idea, modelName, expandToken);
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

		if (usePlanningMode && !ProviderPlanningHelper.SupportsPlanningMode(provider.Type))
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
			idea.Project!,
			idea.Project!.WorkingPath,
			idea.Description,
			expansionPrompt,
			modelName,
			usePlanningMode,
			cancellationToken);

		if (response.Success && !string.IsNullOrWhiteSpace(response.Response))
		{
			if (TryApplyExpandedDescription(idea, response.Response, out var validationError))
			{
				idea.ExpansionStatus = IdeaExpansionStatus.PendingReview;
				idea.ExpandedAt = DateTime.UtcNow;
				_logger.LogInformation("Successfully expanded idea {IdeaId} using CLI provider", idea.Id);
			}
			else
			{
				idea.ExpansionStatus = IdeaExpansionStatus.Failed;
				idea.ExpansionError = validationError;
			}
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
		Project project,
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
			usePlanningMode ? ProviderPlanningHelper.BuildPlanningPrompt(provider.Type, ideaDescription) : expansionPrompt,
			new ExecutionOptions
			{
				WorkingDirectory = workingDirectory,
				UseBareMode = provider.Type == ProviderType.Claude
					&& provider.ConnectionMode == ProviderConnectionMode.CLI,
				Model = modelName,
				ReasoningEffort = usePlanningMode ? project.PlanningReasoningEffort : null,
				DisallowedTools = usePlanningMode ? ProviderPlanningHelper.PlanningDisallowedTools : null
			},
			cancellationToken: cancellationToken);
		var responseText = ProviderPlanningHelper.ExtractExecutionText(result);
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
		if (request?.UseInference == true)
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

	private async Task ExpandWithInferenceAsync(Idea idea, string? modelName, CancellationToken cancellationToken)
	{
		if (_inferenceService == null)
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = "Inference service is not available";
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
			if (TryApplyExpandedDescription(idea, response.Response, out var validationError))
			{
				idea.ExpansionStatus = IdeaExpansionStatus.PendingReview;
				idea.ExpandedAt = DateTime.UtcNow;
				_logger.LogInformation("Successfully expanded idea {IdeaId} using inference (model: {Model})",
					idea.Id, response.ModelUsed ?? modelName ?? "default");
			}
			else
			{
				idea.ExpansionStatus = IdeaExpansionStatus.Failed;
				idea.ExpansionError = validationError;
			}
		}
		else
		{
			idea.ExpansionStatus = IdeaExpansionStatus.Failed;
			idea.ExpansionError = response.Error ?? "No response from inference";
			_logger.LogWarning("Failed to expand idea {IdeaId} with inference: {Error}", idea.Id, idea.ExpansionError);
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
			EnsureLengthWithinLimit("Expanded specification", editedDescription.Trim(), ValidationLimits.IdeaExpandedDescriptionMaxLength);
			idea.ExpandedDescription = editedDescription.Trim();
		}

		idea.ExpansionStatus = IdeaExpansionStatus.Approved;
		idea.ExpandedAt = DateTime.UtcNow;
		ValidateIdea(idea);
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

	private static void ValidateIdea(Idea idea)
	{
		ValidationHelper.ValidateObject(idea);
	}

	private static bool TryApplyExpandedDescription(Idea idea, string expandedDescription, out string validationError)
	{
		var trimmedDescription = expandedDescription.Trim();
		if (trimmedDescription.Length > ValidationLimits.IdeaExpandedDescriptionMaxLength)
		{
			validationError = $"The generated specification exceeded the {ValidationLimits.IdeaExpandedDescriptionMaxLength:N0}-character limit. Shorten the idea or regenerate with a more concise prompt.";
			return false;
		}

		idea.ExpandedDescription = trimmedDescription;
		validationError = string.Empty;
		return true;
	}
}
