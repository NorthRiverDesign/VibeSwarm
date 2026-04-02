using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Shared.Services;

public partial class JobService
{
    public async Task<bool> PauseForInteractionAsync(Guid id, string interactionPrompt, string interactionType,
        string? choices = null, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only allow pausing jobs that are currently processing
        if (job.Status != JobStatus.Processing && job.Status != JobStatus.Planning && job.Status != JobStatus.Started)
        {
            return false;
        }

        job.Status = JobStatus.Paused;
        job.PendingInteractionPrompt = interactionPrompt;
        job.InteractionType = interactionType;
        job.InteractionChoices = choices;
        job.InteractionRequestedAt = DateTime.UtcNow;
        job.CurrentActivity = "Waiting for user input...";
        job.LastActivityAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change and interaction request
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());

                // Parse choices if provided
                List<string>? choicesList = null;
                if (!string.IsNullOrEmpty(choices))
                {
                    try
                    {
                        choicesList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(choices);
                    }
                    catch { }
                }

                await _jobUpdateService.NotifyJobInteractionRequired(job.Id, interactionPrompt, interactionType,
                    choicesList, null);
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        return true;
    }

    public async Task<(string? Prompt, string? Type, string? Choices)?> GetPendingInteractionAsync(Guid id,
        CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null || job.Status != JobStatus.Paused)
        {
            return null;
        }

        return (job.PendingInteractionPrompt, job.InteractionType, job.InteractionChoices);
    }

    public async Task<bool> ResumeJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .Include(j => j.Project)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only allow resuming paused jobs
        if (job.Status != JobStatus.Paused)
        {
            return false;
        }

        job.Status = job.Project?.PlanningEnabled == true &&
            job.Project.PlanningProviderId.HasValue &&
            string.IsNullOrWhiteSpace(job.PlanningOutput)
            ? JobStatus.Planning
            : JobStatus.Processing;
        job.PendingInteractionPrompt = null;
        job.InteractionType = null;
        job.InteractionChoices = null;
        job.InteractionRequestedAt = null;
        job.CurrentActivity = "Resuming...";
        job.LastActivityAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change and resume
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                await _jobUpdateService.NotifyJobResumed(job.Id);
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        return true;
    }

    public async Task<bool> ContinueJobAsync(Guid id, string followUpPrompt, CancellationToken cancellationToken = default)
    {
        var trimmedFollowUp = followUpPrompt?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedFollowUp))
        {
            return false;
        }

        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null || job.Status != JobStatus.Completed)
        {
            return false;
        }

        var submittedAt = DateTime.UtcNow;
        var message = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Role = MessageRole.User,
            Content = trimmedFollowUp,
            CreatedAt = submittedAt
        };

        _dbContext.JobMessages.Add(message);

        job.GoalPrompt = BuildContinuationPrompt(job.GoalPrompt, trimmedFollowUp);
        await ResetJobForFollowUp(job, submittedAt, cancellationToken);
        await InitializeExecutionPlanAsync(job, cancellationToken);
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

    public async Task<IEnumerable<Job>> GetPausedJobsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Include(j => j.Project)
            .Include(j => j.Provider)
            .Where(j => j.Status == JobStatus.Paused)
            .OrderByDescending(j => j.InteractionRequestedAt ?? j.LastActivityAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> GetLastUsedModelAsync(Guid projectId, Guid providerId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId && j.ProviderId == providerId && j.ModelUsed != null)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => j.ModelUsed)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> ResetJobWithOptionsAsync(Guid id, Guid? providerId = null, string? modelId = null, string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Do not allow resetting completed jobs
        if (job.Status == JobStatus.Completed)
        {
            return false;
        }

        // If provider is being changed, verify it exists and is enabled
        if (providerId.HasValue && providerId.Value != job.ProviderId)
        {
            var provider = await _dbContext.Providers
                .FirstOrDefaultAsync(p => p.Id == providerId.Value && p.IsEnabled, cancellationToken);
            if (provider == null)
            {
                return false;
            }
            job.ProviderId = providerId.Value;
        }

        // Reset job to initial state while preserving the original configuration
        job.Status = JobStatus.New;
        job.CancellationRequested = false;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.Output = null;
        job.ErrorMessage = null;
        job.CurrentActivity = null;
        job.LastActivityAt = null;
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
        job.GitCheckpointStatus = GitCheckpointStatus.None;
        job.GitCheckpointBranch = null;
        job.GitCheckpointBaseBranch = null;
        job.GitCheckpointCommitHash = null;
        job.GitCheckpointReason = null;
        job.GitCheckpointCapturedAt = null;
        job.PullRequestNumber = null;
        job.PullRequestUrl = null;
        job.PullRequestCreatedAt = null;
        job.MergedAt = null;
        job.PlanningOutput = null;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningReasoningEffortUsed = null;
        job.PlanningGeneratedAt = null;
        job.PlanningInputTokens = null;
        job.PlanningOutputTokens = null;
        job.PlanningCostUsd = null;
        job.SessionId = null; // Clear session for fresh start with potentially new provider
        job.ModelUsed = modelId; // Set the requested model (null means use provider default)
        job.ReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(reasoningEffort);
        job.RetryCount++; // Increment retry count to track attempts
        job.ActiveExecutionIndex = 0;
        job.ExecutionPlan = null;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;

        // Clear token/cost tracking for fresh run
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

        await InitializeExecutionPlanAsync(job, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change
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
}
