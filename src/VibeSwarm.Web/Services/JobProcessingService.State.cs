using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
    /// <summary>
    /// Releases ownership of a job and sets final status
    /// </summary>
    private async Task ReleaseJobAsync(Guid jobId, JobStatus status, string? errorMessage, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            JobStateMachine.TryTransition(job, status, errorMessage);
            job.WorkerInstanceId = null;
            job.LastHeartbeatAt = null;
            job.ProcessId = null;
            job.CurrentActivity = null;
            job.ErrorMessage = errorMessage;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Updates job status
    /// </summary>
    private async Task UpdateJobStatusAsync(Guid jobId, JobStatus status, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            var transitioned = JobStateMachine.TryTransition(job, status, $"Internal transition to {status}.");
            if (!transitioned.Success)
            {
                _logger.LogWarning("Failed to update job {JobId} to {Status}: {Error}", jobId, status, transitioned.ErrorMessage);
                return;
            }

            job.LastHeartbeatAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Updates heartbeat and current activity
    /// </summary>
    private async Task UpdateHeartbeatAsync(Guid jobId, string? activity, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job != null)
        {
            job.LastHeartbeatAt = DateTime.UtcNow;
            job.LastActivityAt = DateTime.UtcNow;
            job.CurrentActivity = activity;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Re-queues a job that was rate-limited instead of marking it as failed.
    /// Does not consume a retry attempt since this is a provider-side throttle, not a job failure.
    /// </summary>
    private async Task RequeueJobForRateLimitAsync(
        Guid jobId, Guid providerId, string providerName, string resetDescription,
        ExecutionResult result, JobExecutionContext executionContext,
        string? workingDirectory, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FindAsync(new object[] { jobId }, cancellationToken);
        if (job == null) return;

        // Store any partial console output
        var consoleOutput = executionContext.GetConsoleOutput();
        if (!string.IsNullOrEmpty(consoleOutput))
        {
            job.ConsoleOutput = consoleOutput;
        }

        // Transition back to New so the job queue will pick it up again once the provider recovers.
        // This does NOT consume a retry attempt since rate limiting is a provider-side throttle.
        var transition = JobStateMachine.TryTransition(job, JobStatus.New,
            $"Rate limited by {providerName}. {resetDescription}");
        if (!transition.Success)
        {
            _logger.LogWarning("Failed to re-queue rate-limited job {JobId}: {Error}", jobId, transition.ErrorMessage);
            return;
        }

        job.ErrorMessage = $"Rate limited by {providerName}. {resetDescription}. Waiting for provider to become available.";
        job.LastActivityAt = DateTime.UtcNow;

        // Accumulate partial token usage if any
        if (result.InputTokens.HasValue)
            job.InputTokens = (job.InputTokens ?? 0) + result.InputTokens.Value;
        if (result.OutputTokens.HasValue)
            job.OutputTokens = (job.OutputTokens ?? 0) + result.OutputTokens.Value;
        if (result.CostUsd.HasValue)
            job.TotalCostUsd = (job.TotalCostUsd ?? 0) + result.CostUsd.Value;

        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Job {JobId} re-queued after rate limit from {ProviderName}. {ResetDesc}",
            jobId, providerName, resetDescription);
    }

    /// <summary>
    /// Completes a job with full result data, console output, and git diff
    /// </summary>
    private async Task<bool> CompleteJobAsync(
        Guid jobId, JobStatus status, string? sessionId, string? output, string? errorMessage,
        int? inputTokens, int? outputTokens, decimal? costUsd, string? modelUsed,
        JobExecutionContext executionContext, string? workingDirectory,
        VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
    {
        var hasGitChanges = false;
        var job = await dbContext.Jobs
            .Include(j => j.Project)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job != null)
        {
            var transition = JobStateMachine.TryTransition(job, status, errorMessage);
            if (!transition.Success)
            {
                _logger.LogWarning("Failed to complete job {JobId}: {Error}", jobId, transition.ErrorMessage);
                return false;
            }

            job.SessionId = sessionId ?? job.SessionId;
            job.Output = output ?? job.Output;
            job.ErrorMessage = errorMessage;
            job.InputTokens = inputTokens ?? job.InputTokens;
            job.OutputTokens = outputTokens ?? job.OutputTokens;
            job.TotalCostUsd = costUsd ?? job.TotalCostUsd;
            job.ModelUsed = modelUsed ?? job.ModelUsed;
            job.WorkerInstanceId = null;
            job.LastHeartbeatAt = null;
            job.ProcessId = null;
            job.CurrentActivity = null;

            // Store accumulated console output
            var consoleOutput = executionContext.GetConsoleOutput();
            if (!string.IsNullOrEmpty(consoleOutput))
            {
                job.ConsoleOutput = consoleOutput;
            }

            // Generate and store git diff if applicable
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                try
                {
                    // Brief delay to let git release file locks after the agent process exits
                    await Task.Delay(750, cancellationToken);

                    var baseCommit = executionContext.GitCommitBefore;
                    var (gitDiff, commitLog) = await CaptureGitDiffWithRetryAsync(
                        workingDirectory, baseCommit, cancellationToken);

                    if (!string.IsNullOrEmpty(gitDiff))
                    {
                        job.GitDiff = gitDiff;
                        hasGitChanges = true;
                        _logger.LogInformation("Captured git diff for job {JobId}: {Length} chars", jobId, gitDiff.Length);

                        // Count changed files for the badge/toast
                        try
                        {
                            var changedFiles = await _versionControlService.GetChangedFilesAsync(workingDirectory, baseCommit, cancellationToken);
                            job.ChangedFilesCount = changedFiles.Count;
                            _logger.LogInformation("Job {JobId} changed {Count} file(s)", jobId, changedFiles.Count);
                        }
                        catch (Exception cfEx)
                        {
                            _logger.LogWarning(cfEx, "Failed to count changed files for job {JobId}", jobId);
                        }

                        // Generate session summary from git diff for pre-populating commit messages
                        // Pass the commit log so we can include agent commit messages as bullet points
                        var sessionSummary = JobSummaryGenerator.GenerateSummary(job, commitLog);
                        if (!string.IsNullOrWhiteSpace(sessionSummary))
                        {
                            job.SessionSummary = sessionSummary;
                            _logger.LogInformation("Generated session summary for job {JobId}: {Summary}",
                                jobId, sessionSummary.Length > 100 ? sessionSummary[..100] + "..." : sessionSummary);
                        }
                    }
                    else
                    {
                        job.ChangedFilesCount = 0;
                        _logger.LogDebug("No git changes detected for job {JobId}", jobId);
                    }

                    await TryRecordAgentCommitAsync(job, workingDirectory, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture git diff for job {JobId}", jobId);
                }

                // Perform auto-commit if configured and job completed successfully
                // Also auto-commit when IdeasAutoCommit is true (even if project-level AutoCommitMode is Off)
                if (status == JobStatus.Completed && ShouldProcessGitDelivery(job))
                {
                    // Run build/test verification before committing if enabled
                    var buildPassed = await VerifyBuildAsync(job, workingDirectory, cancellationToken);
                    if (buildPassed)
                    {
                        await PerformAutoCommitAsync(job, workingDirectory, cancellationToken);
                        await CreatePullRequestIfConfiguredAsync(job, workingDirectory, cancellationToken);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Skipping auto-commit and push for job {JobId} because build verification failed. " +
                            "Changes remain uncommitted in {WorkingDirectory} for manual review.",
                            job.Id, workingDirectory);
                    }
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return hasGitChanges;
    }
}
