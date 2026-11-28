using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

public partial class JobService
{
    public async Task RefreshExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .Include(j => j.Statistics)
            .Include(j => j.PlanningStatistics)
            .Include(j => j.ExecutionStatistics)
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

        NormalizeJobForPersistence(job);
        job.ExecutionPlan = null;
        await InitializeExecutionPlanAsync(job, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JobStep>> GetStepsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.JobSteps
            .Where(step => step.JobId == jobId)
            .OrderBy(step => step.Order)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<JobStep?> AddStepAsync(Guid jobId, string instructions, CancellationToken cancellationToken = default)
    {
        var text = instructions?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var job = await _dbContext.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new { j.Status, j.TeamRun })
            .FirstOrDefaultAsync(cancellationToken);
        var steps = await _dbContext.JobSteps.Where(step => step.JobId == jobId).ToListAsync(cancellationToken);

        // Only a chain still being worked can take another step: once its last step is done the
        // run is wrapping up, and a step added then would never be built.
        if (job is not { TeamRun: true } || !JobChain.IsOpen(job.Status)
            || !steps.Any(step => step.Status is JobStepStatus.Waiting or JobStepStatus.Running)
            || steps.Count(step => step.Status == JobStepStatus.Waiting) >= JobChain.MaxWaitingSteps)
        {
            return null;
        }

        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var added = new JobStep
        {
            JobId = jobId,
            Order = steps.Max(step => step.Order) + 1,
            Title = firstLine.Length <= 80 ? firstLine : firstLine[..79].TrimEnd() + "…",
            Instructions = text.Length <= JobStep.TextMaxLength ? text : text[..JobStep.TextMaxLength],
            Source = JobStepSource.User
        };
        _dbContext.JobSteps.Add(added);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await NotifyStepsChangedAsync(jobId);
        return added;
    }

    public async Task<bool> SkipStepAsync(Guid jobId, Guid stepId, CancellationToken cancellationToken = default)
    {
        // One statement, so a step the runner has just started can't be skipped under it.
        var skipped = await _dbContext.JobSteps
            .Where(step => step.Id == stepId && step.JobId == jobId && step.Status == JobStepStatus.Waiting)
            .ExecuteUpdateAsync(setters => setters.SetProperty(step => step.Status, JobStepStatus.Skipped), cancellationToken);
        if (skipped == 0)
        {
            return false;
        }

        await NotifyStepsChangedAsync(jobId);
        return true;
    }

    /// <summary>
    /// What a job queued again (a retry, a reset, a move to another provider) does with its plan.
    /// A team job the Lead cut into steps keeps them: the next run carries on at the first step
    /// not finished, from the work its finished steps saved, and a step that was under way
    /// starts over. Any other job is planned afresh.
    /// </summary>
    private async Task KeepOrClearChainAsync(Job job, CancellationToken cancellationToken)
    {
        var steps = job.TeamRun
            ? await _dbContext.JobSteps
                .Where(step => step.JobId == job.Id && step.Status != JobStepStatus.Earlier)
                .OrderBy(step => step.Order)
                .ToListAsync(cancellationToken)
            : [];
        if (steps.Count > 0)
        {
            foreach (var step in steps.Where(step => step.Status == JobStepStatus.Running))
            {
                step.Status = JobStepStatus.Waiting;
                step.SessionId = null;
                step.StartedAt = null;
            }

            // What the finished steps' reviews cost stays on the job with them.
            var (reviewCost, reviewer) = (job.ReviewCostUsd, job.ReviewProviderName);
            JobReview.Reset(job);
            (job.ReviewCostUsd, job.ReviewProviderName) = (reviewCost, reviewer);
            job.WorkSnapshotCommit = steps.LastOrDefault(step => step.Status == JobStepStatus.Done && step.SnapshotCommit != null)?.SnapshotCommit;
            return;
        }

        job.PlanningOutput = null;
        job.PlanSkippedReason = null;
        job.LeadChecks = 0;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningReasoningEffortUsed = null;
        JobReview.Reset(job);
        job.PlanningGeneratedAt = null;
        job.PlanningInputTokens = null;
        job.PlanningOutputTokens = null;
        job.PlanningCostUsd = null;
    }

    private async Task NotifyStepsChangedAsync(Guid jobId)
    {
        if (_jobUpdateService == null)
        {
            return;
        }

        try
        {
            await _jobUpdateService.NotifyJobStepsChanged(jobId);
        }
        catch
        {
            // The page also reloads its steps when the job's status changes.
        }
    }

    public async Task<IEnumerable<JobChangeSet>> GetChangeSetsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.JobChangeSets
            .Where(cs => cs.JobId == jobId)
            .OrderBy(cs => cs.FollowUpIndex)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Moves a failed job onto the next provider in its execution plan and queues it again.
    /// This is what makes a provider running out of quota a detour rather than a dead end:
    /// the plan was always ordered, but nothing used to advance through it.
    /// </summary>
    /// <returns>True when the job was handed to another provider; false when the plan is spent.</returns>
    public async Task<bool> TryFailOverToNextExecutionTargetAsync(
        Guid id,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .Include(j => j.Statistics)
            .Include(j => j.PlanningStatistics)
            .Include(j => j.ExecutionStatistics)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        // A job the user stopped is not looking for another provider to try.
        if (job == null || job.CancellationRequested || string.IsNullOrWhiteSpace(job.ExecutionPlan))
        {
            return false;
        }

        List<JobExecutionTarget>? plan;
        try
        {
            plan = JsonSerializer.Deserialize<List<JobExecutionTarget>>(job.ExecutionPlan);
        }
        catch (JsonException)
        {
            return false;
        }

        if (plan == null || plan.Count == 0)
        {
            return false;
        }

        var next = await FindNextUsableTargetAsync(job, plan, cancellationToken);
        if (next == null)
        {
            return false;
        }

        var (nextIndex, target) = next.Value;
        var previousProviderName = plan[Math.Clamp(job.ActiveExecutionIndex, 0, plan.Count - 1)].ProviderName;
        var savedPlan = job.ExecutionPlan;
        var (askedProfileId, askedProfileName) = (job.ProfileId, job.ProfileName);

        if (!await ResetJobWithOptionsInternalAsync(job, target.ProviderId, target.ModelId, target.ReasoningEffort, null, cancellationToken))
        {
            return false;
        }

        // The reset clears the plan so a manual retry starts fresh; a failover has to keep
        // it and step forward, otherwise the job would start again on the provider that
        // just failed. It also keeps the profile the job was asked to run on: the switch is
        // the app's doing, and a later retry goes back to what was asked for.
        job.ProfileId = askedProfileId;
        job.ProfileName = askedProfileName;
        job.ExecutionPlan = savedPlan;
        job.ActiveExecutionIndex = nextIndex;
        job.LastSwitchAt = DateTime.UtcNow;
        job.LastSwitchReason = BuildSwitchReason(previousProviderName, target.ProviderName, reason);
        job.CurrentActivity = $"Switched to {target.ProviderName}";

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return true;
    }

    /// <summary>
    /// Walks forward through the plan for a provider that could actually take the job:
    /// enabled, and not already out of quota. Never walks backwards, so failover always
    /// terminates.
    /// </summary>
    private async Task<(int Index, JobExecutionTarget Target)?> FindNextUsableTargetAsync(
        Job job,
        List<JobExecutionTarget> plan,
        CancellationToken cancellationToken)
    {
        var usageService = _serviceProvider.GetService(typeof(IProviderUsageService)) as IProviderUsageService;

        for (var index = job.ActiveExecutionIndex + 1; index < plan.Count; index++)
        {
            var candidate = plan[index];

            var provider = await _dbContext.Providers
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == candidate.ProviderId && p.IsEnabled, cancellationToken);

            if (provider == null)
            {
                continue;
            }

            if (usageService != null)
            {
                try
                {
                    var exhaustion = await usageService.CheckExhaustionAsync(candidate.ProviderId, cancellationToken: cancellationToken);
                    if (exhaustion?.IsExhausted == true)
                    {
                        continue;
                    }
                }
                catch
                {
                    // Usage tracking is advisory; a failure to read it must not block failover.
                }
            }

            return (index, candidate);
        }

        return null;
    }

    private static string BuildSwitchReason(string? from, string? to, string? reason)
    {
        var summary = ProviderFailureClassifier.Summarize(reason);
        var text = string.IsNullOrWhiteSpace(summary)
            ? $"{from} failed; trying {to}"
            : $"{from} failed ({summary}); trying {to}";

        return text.Length <= 200 ? text : text[..199] + "\u2026";
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
        const int maxPromptLength = ValidationLimits.JobGoalPromptMaxLength;
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
        job.PlanningCommandUsed = null;
        job.ExecutionCommandUsed = null;
        job.ConsoleOutput = null;
        job.GitDiff = null;
        job.GitCommitBefore = null;
        job.GitCommitHash = null;
        job.WorkSnapshotCommit = null;
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
        job.PlanSkippedReason = null;
        job.LeadChecks = 0;
        // The follow-up is planned afresh. The steps already built stay as the job's record, and
        // the new plan's steps follow them; steps that were never built go with the old plan.
        await _dbContext.JobSteps
            .Where(step => step.JobId == job.Id && step.Status == JobStepStatus.Done)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(step => step.Status, JobStepStatus.Earlier)
                .SetProperty(step => step.SnapshotCommit, (string?)null), cancellationToken);
        await _dbContext.JobSteps
            .Where(step => step.JobId == job.Id && (step.Status == JobStepStatus.Waiting || step.Status == JobStepStatus.Running))
            .ExecuteDeleteAsync(cancellationToken);
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningReasoningEffortUsed = null;
        JobReview.Reset(job);
        job.PlanningGeneratedAt = null;
        job.PlanningInputTokens = null;
        job.PlanningOutputTokens = null;
        job.PlanningCostUsd = null;
        job.ActiveExecutionIndex = 0;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;
        JobRecoveryHelper.ClearRecoveryState(job);
        job.InputTokens = null;
        job.OutputTokens = null;
        job.TotalCostUsd = null;
        job.ExecutionInputTokens = null;
        job.ExecutionOutputTokens = null;
        job.ExecutionCostUsd = null;

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

        // A job run on a profile was given its model by that profile, so nothing the project
        // prefers for the same provider may replace it.
        var runsOnProfile = job.ProfileId.HasValue;
        var providerOrder = BuildProviderOrder(job.ProviderId, enabledProviders, projectSelections, runsOnProfile);

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
                else if (!runsOnProfile && !string.IsNullOrWhiteSpace(selection?.PreferredModelId))
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
        if (job.JobTemplateId.HasValue)
        {
            var templateExists = await _dbContext.JobTemplates
                .AnyAsync(template => template.Id == job.JobTemplateId.Value, cancellationToken);
            if (!templateExists)
            {
                throw new InvalidOperationException("The selected job template no longer exists.");
            }
        }

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

	/// <summary>
	/// Decides what a new job runs on, once, for every way of creating one:
	/// a profile the job names wins over anything else it was sent; a job that names neither a
	/// profile nor a provider takes the project's Build profile (the first it lists, or the
	/// default provider's own when it lists none); a job that names only a
	/// provider (an older client) is left as sent. A profile that is gone, or whose provider is
	/// off, counts as not named, so a schedule or auto-pilot run falls through instead of failing.
	/// </summary>
	private async Task ApplyJobProfileAsync(Job job, CancellationToken cancellationToken)
	{
		var resolved = await ResolveJobProfileAsync(job.ProfileId, cancellationToken);
		if (resolved == null && job.ProviderId == Guid.Empty)
		{
			resolved = await ResolveBuildProfileAsync(job.ProjectId, cancellationToken);
		}

		ApplyResolvedProfile(job, resolved);
	}

	/// <summary>
	/// The profile the project builds with: its first enabled Build entry's, or the default
	/// provider's own when the project lists nothing.
	/// </summary>
	private async Task<ResolvedJobProfile?> ResolveBuildProfileAsync(Guid projectId, CancellationToken cancellationToken)
	{
		var buildOrder = await _dbContext.ProjectProviders
			.Where(entry => entry.ProjectId == projectId && entry.IsEnabled
				&& _dbContext.Providers.Any(provider => provider.Id == entry.ProviderId && provider.IsEnabled))
			.OrderBy(entry => entry.Priority)
			.Select(entry => entry.ProfileId)
			.ToListAsync(cancellationToken);
		var buildProfileId = buildOrder.Count > 0
			? buildOrder[0]
			: await _dbContext.Profiles
				.Where(profile => profile.IsProviderDefault && profile.Provider != null && profile.Provider.IsEnabled)
				.OrderByDescending(profile => profile.Provider!.IsDefault)
				.ThenBy(profile => profile.Provider!.Name)
				.Select(profile => (Guid?)profile.Id)
				.FirstOrDefaultAsync(cancellationToken);
		return await ResolveJobProfileAsync(buildProfileId, cancellationToken);
	}

	/// <summary>Puts the profile's values on the job, or clears the job's profile when there is none.</summary>
	private static void ApplyResolvedProfile(Job job, ResolvedJobProfile? resolved)
	{
		if (resolved is not { } profile)
		{
			job.ProfileId = null;
			job.ProfileName = null;
			return;
		}

		job.ProfileId = profile.Id;
		job.ProfileName = profile.Name;
		job.ProviderId = profile.ProviderId;
		job.ModelUsed = profile.ModelId;
		job.ReasoningEffort = profile.ReasoningEffort;
	}

	/// <summary>
	/// The provider, model and reasoning a job gets from a profile. Null when the profile is
	/// missing, answers questions only, or sits on a provider that is turned off.
	/// </summary>
	private async Task<ResolvedJobProfile?> ResolveJobProfileAsync(Guid? profileId, CancellationToken cancellationToken)
	{
		if (profileId is not Guid id)
		{
			return null;
		}

		var profile = await _dbContext.Profiles
			.AsNoTracking()
			.Include(candidate => candidate.Provider)
			.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
		if (profile == null || !ProfileRoles.CanBuild(profile))
		{
			return null;
		}

		var defaultModelId = string.IsNullOrWhiteSpace(profile.ModelId)
			? await _dbContext.ProviderModels
				.Where(model => model.ProviderId == profile.ProviderId && model.IsAvailable && model.IsDefault)
				.Select(model => model.ModelId)
				.FirstOrDefaultAsync(cancellationToken)
			: null;
		var (providerId, modelId, reasoningEffort) = ProfileLinks.ResolveForJob(profile, defaultModelId);
		return new ResolvedJobProfile(profile.Id, profile.Name, providerId, modelId, reasoningEffort);
	}

	private readonly record struct ResolvedJobProfile(Guid Id, string Name, Guid ProviderId, string? ModelId, string? ReasoningEffort);

    private static List<Provider> BuildProviderOrder(Guid selectedProviderId, List<Provider> enabledProviders, List<ProjectProvider> projectSelections, bool runsOnProfile)
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

        // A profile picked for this job runs first even on a provider the project doesn't list.
        // Switching away from it afterwards still follows the project's list alone.
        if (runsOnProfile
            && orderedProviders.All(provider => provider.Id != selectedProviderId)
            && providerById.TryGetValue(selectedProviderId, out var pickedProvider))
        {
            return [pickedProvider, .. orderedProviders];
        }

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
