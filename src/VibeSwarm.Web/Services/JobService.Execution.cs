using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public partial class JobService
{
    public async Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .Include(j => j.Project)
                .ThenInclude(p => p!.ProviderSelections)
            .Include(j => j.Provider)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return;
        }

        // Only refresh for jobs that haven't started processing yet
        if (job.Status != JobStatus.New)
        {
            return;
        }

        job.ExecutionPlan = null;
        await InitializeExecutionPlanAsync(job, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task InitializeExecutionPlanAsync(Job job, CancellationToken cancellationToken)
    {
        var targets = await BuildExecutionPlanAsync(job, cancellationToken);
        job.ExecutionPlan = targets.Count > 0 ? JsonSerializer.Serialize(targets) : null;
        job.ActiveExecutionIndex = 0;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;

		if (job.ProviderId == Guid.Empty && targets.Count > 0)
		{
			job.ProviderId = targets[0].ProviderId;
		}

        if (string.IsNullOrWhiteSpace(job.ModelUsed) && targets.Count > 0)
        {
            job.ModelUsed = targets[0].ModelId;
        }

        if (string.IsNullOrWhiteSpace(job.ReasoningEffort) && targets.Count > 0)
        {
            job.ReasoningEffort = targets[0].ReasoningEffort;
        }
    }

    private static string BuildContinuationPrompt(string previousGoalPrompt, string followUpPrompt)
    {
        const int maxPromptLength = 2000;
        const string templatePrefix = "Continue the previous job for this project.\nPrevious goal: ";
        const string templateMiddle = "\n\nFollow-up instructions:\n";

        var trimmedFollowUp = followUpPrompt.Trim();
        var maxFollowUpLength = Math.Max(0, maxPromptLength - templatePrefix.Length - templateMiddle.Length);
        if (trimmedFollowUp.Length > maxFollowUpLength)
        {
            trimmedFollowUp = maxFollowUpLength <= 3
                ? trimmedFollowUp[..maxFollowUpLength]
                : trimmedFollowUp[..(maxFollowUpLength - 3)] + "...";
        }

        var reservedLength = templatePrefix.Length + templateMiddle.Length + trimmedFollowUp.Length;
        var availableForPreviousGoal = Math.Max(0, maxPromptLength - reservedLength);
        var previousGoalSnippet = previousGoalPrompt;

        if (previousGoalSnippet.Length > availableForPreviousGoal)
        {
            previousGoalSnippet = availableForPreviousGoal switch
            {
                <= 0 => string.Empty,
                <= 3 => previousGoalSnippet[..availableForPreviousGoal],
                _ => previousGoalSnippet[..(availableForPreviousGoal - 3)] + "..."
            };
        }

        return $"{templatePrefix}{previousGoalSnippet}{templateMiddle}{trimmedFollowUp}";
    }

    private async Task ResetJobForFollowUp(Job job, DateTime submittedAt, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.New;
        job.CancellationRequested = false;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.Output = null;
        job.ErrorMessage = null;
        job.CurrentActivity = "Queued follow-up instructions...";
        job.LastActivityAt = submittedAt;
        job.WorkerInstanceId = null;
        job.LastHeartbeatAt = null;
        job.ProcessId = null;
        job.CommandUsed = null;
        job.ConsoleOutput = null;
        job.GitDiff = null;
        job.GitCommitBefore = null;
        job.GitCommitHash = null;
        job.PullRequestNumber = null;
        job.PullRequestUrl = null;
        job.PullRequestCreatedAt = null;
        job.MergedAt = null;
        job.SessionSummary = null;
        job.ChangedFilesCount = null;
        job.BuildVerified = null;
        job.BuildOutput = null;
        job.PendingInteractionPrompt = null;
        job.InteractionType = null;
        job.InteractionChoices = null;
        job.InteractionRequestedAt = null;
        job.PlanningOutput = null;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningReasoningEffortUsed = null;
        job.PlanningGeneratedAt = null;
        job.CurrentCycle = 1;
        job.ActiveExecutionIndex = 0;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;
        job.InputTokens = null;
        job.OutputTokens = null;
        job.TotalCostUsd = null;

        var attempts = await _dbContext.JobProviderAttempts
            .Where(a => a.JobId == job.Id)
            .ToListAsync(cancellationToken);
        if (attempts.Count > 0)
        {
            _dbContext.JobProviderAttempts.RemoveRange(attempts);
        }
    }

    private async Task<List<JobExecutionTarget>> BuildExecutionPlanAsync(Job job, CancellationToken cancellationToken)
    {
        var enabledProviders = await _dbContext.Providers
            .Where(p => p.IsEnabled)
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);

        if (enabledProviders.Count == 0)
        {
            return [];
        }

        var providerIds = enabledProviders.Select(p => p.Id).ToList();
        var projectSelections = await _dbContext.ProjectProviders
            .Where(pp => pp.ProjectId == job.ProjectId && pp.IsEnabled && providerIds.Contains(pp.ProviderId))
            .OrderBy(pp => pp.Priority)
            .ToListAsync(cancellationToken);

		var providerOrder = BuildProviderOrder(job.ProviderId, enabledProviders, projectSelections);

        var modelLookup = await _dbContext.ProviderModels
            .Where(m => providerIds.Contains(m.ProviderId) && m.IsAvailable)
            .OrderByDescending(m => m.IsDefault)
            .ThenBy(m => m.DisplayName ?? m.ModelId)
            .ToListAsync(cancellationToken);

        var modelsByProvider = modelLookup
            .GroupBy(m => m.ProviderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var targets = new List<JobExecutionTarget>();
        var order = 0;

        foreach (var provider in providerOrder)
        {
            var selection = projectSelections.FirstOrDefault(pp => pp.ProviderId == provider.Id);
            modelsByProvider.TryGetValue(provider.Id, out var providerModels);
            providerModels ??= [];

            var plannedTargets = new List<(string? ModelId, string? ReasoningEffort, string Source)>();
            var selectedReasoning = provider.Id == job.ProviderId
                ? job.ReasoningEffort
                : selection?.PreferredReasoningEffort ?? provider.DefaultReasoningEffort;
            if (provider.Id == job.ProviderId)
            {
                if (!string.IsNullOrWhiteSpace(job.ModelUsed))
                {
                    plannedTargets.Add((job.ModelUsed, selectedReasoning, "job-selected-model"));
                }
                else if (!string.IsNullOrWhiteSpace(selection?.PreferredModelId))
                {
                    plannedTargets.Add((selection.PreferredModelId, selectedReasoning, "project-preferred-model"));
                }
            }
            else if (!string.IsNullOrWhiteSpace(selection?.PreferredModelId))
            {
                plannedTargets.Add((selection.PreferredModelId, selectedReasoning, "project-preferred-model"));
            }

            var defaultModel = providerModels.FirstOrDefault(m => m.IsDefault);
            if (defaultModel != null)
            {
                plannedTargets.Add((defaultModel.ModelId, selectedReasoning, "provider-default-model"));
            }

            foreach (var model in providerModels)
            {
                plannedTargets.Add((model.ModelId, selectedReasoning, "provider-available-model"));
            }

            if (plannedTargets.Count == 0)
            {
                plannedTargets.Add((null, selectedReasoning, "provider-default-model"));
            }

            foreach (var candidate in plannedTargets)
            {
                if (targets.Any(existing =>
                    existing.ProviderId == provider.Id &&
                    existing.ModelId == candidate.ModelId &&
                    existing.ReasoningEffort == candidate.ReasoningEffort))
                {
                    continue;
                }

                targets.Add(new JobExecutionTarget
                {
                    ProviderId = provider.Id,
                    ProviderName = provider.Name,
                    ModelId = candidate.ModelId,
                    ReasoningEffort = candidate.ReasoningEffort,
                    Order = order++,
                    Source = candidate.Source
                });
            }
        }

        return targets;
    }

    private async Task ValidateRequestedExecutionAsync(Job job, CancellationToken cancellationToken)
    {
		if (job.ProviderId == Guid.Empty)
		{
			if (!string.IsNullOrWhiteSpace(job.ModelUsed))
			{
				throw new InvalidOperationException("Selecting a model requires selecting a provider.");
            }

            if (!string.IsNullOrWhiteSpace(job.ReasoningEffort))
            {
                throw new InvalidOperationException("Selecting a reasoning level requires selecting a provider.");
            }

            return;
        }

		var provider = await _dbContext.Providers
			.AsNoTracking()
			.FirstOrDefaultAsync(provider => provider.Id == job.ProviderId && provider.IsEnabled, cancellationToken);
        if (provider == null)
        {
            throw new InvalidOperationException("The selected provider is not enabled.");
        }

        if (!ProviderCapabilities.SupportsReasoningEffort(provider, job.ReasoningEffort))
        {
            throw new InvalidOperationException("The selected reasoning level is not supported by the chosen provider.");
        }

        if (string.IsNullOrWhiteSpace(job.ModelUsed))
        {
            return;
        }

        var modelExists = await _dbContext.ProviderModels
            .AnyAsync(model =>
				model.ProviderId == job.ProviderId &&
				model.IsAvailable &&
				model.ModelId == job.ModelUsed,
				cancellationToken);
        if (!modelExists)
        {
            throw new InvalidOperationException("The selected model is not available for the chosen provider.");
        }
    }

	private static List<Provider> BuildProviderOrder(Guid selectedProviderId, List<Provider> enabledProviders, List<ProjectProvider> projectSelections)
    {
        if (projectSelections.Count == 0)
        {
            return OrderProvidersWithSelectionFirst(selectedProviderId, enabledProviders);
        }

        var providerById = enabledProviders.ToDictionary(p => p.Id);
        var orderedProviders = projectSelections
            .Where(pp => providerById.ContainsKey(pp.ProviderId))
            .Select(pp => providerById[pp.ProviderId])
            .ToList();

        return OrderProvidersWithSelectionFirst(selectedProviderId, orderedProviders);
    }

	private static List<Provider> OrderProvidersWithSelectionFirst(Guid selectedProviderId, List<Provider> providers)
	{
		if (selectedProviderId == Guid.Empty)
		{
			return providers;
		}

		var selectedProvider = providers.FirstOrDefault(p => p.Id == selectedProviderId);
        if (selectedProvider == null)
        {
            return providers;
        }

        return [selectedProvider, .. providers.Where(p => p.Id != selectedProviderId)];
    }
}
