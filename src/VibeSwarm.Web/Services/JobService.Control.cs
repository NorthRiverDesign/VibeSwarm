using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Shared.Services;

public partial class JobService
{
    public async Task<bool> RequestCancellationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only allow cancellation for jobs that are not already completed
        if (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed || job.Status == JobStatus.Cancelled)
        {
            return false;
        }

        job.CancellationRequested = true;
        job.LastActivityAt = DateTime.UtcNow; // Update activity time to track when cancellation was requested

        // If job hasn't started yet, mark it as cancelled immediately
        if (job.Status == JobStatus.New || job.Status == JobStatus.Pending)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.CurrentActivity = null;
            job.WorkerInstanceId = null;
            job.ProcessId = null;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
            }
            catch { }
        }

        // Handle idea state when job is immediately cancelled (New/Pending)
        if (job.Status == JobStatus.Cancelled)
        {
            try
            {
                var ideaService = _serviceProvider.GetService<IIdeaService>();
                if (ideaService != null)
                {
                    await ideaService.HandleJobCompletionAsync(job.Id, false, cancellationToken);
                }
            }
            catch { /* Ignore errors handling idea state */ }
        }

        _jobProcessingService?.TriggerProcessing();

        return true;
    }

    public async Task<bool> ForceCancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only force cancel jobs that are actively running
        if (job.Status != JobStatus.Started && job.Status != JobStatus.Planning && job.Status != JobStatus.Processing)
        {
            return false;
        }

        // Try to kill the process if we have a PID
        if (job.ProcessId.HasValue)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(job.ProcessId.Value);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Process already exited
            }
            catch (Exception)
            {
                // Ignore errors killing process
            }
        }

        // Force the job to cancelled state
        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTime.UtcNow;
        job.CancellationRequested = true;
        job.CurrentActivity = null;
        job.WorkerInstanceId = null;
        job.ProcessId = null;
        job.ErrorMessage = "Job was force-cancelled by user.";

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about completion
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobCompleted(job.Id, false, job.ErrorMessage);
            }
            catch { }
        }

        // Handle idea state when job is force-cancelled
        try
        {
            var ideaService = _serviceProvider.GetService<IIdeaService>();
            if (ideaService != null)
            {
                await ideaService.HandleJobCompletionAsync(job.Id, false, cancellationToken);
            }
        }
        catch { /* Ignore errors handling idea state */ }

        return true;
    }

    public async Task<bool> IsCancellationRequestedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        return job?.CancellationRequested ?? false;
    }

    public async Task<bool> ResetJobAsync(Guid id, CancellationToken cancellationToken = default)
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
        job.ConsoleOutput = null;  // Clear accumulated console output
        job.GitDiff = null;        // Clear git diff
        job.GitCommitBefore = null; // Clear git commit reference
        job.GitCommitHash = null;  // Clear committed results hash
        job.PlanningOutput = null;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningReasoningEffortUsed = null;
        job.PlanningGeneratedAt = null;
        job.PlanningInputTokens = null;
        job.PlanningOutputTokens = null;
        job.PlanningCostUsd = null;
        job.GitCheckpointStatus = GitCheckpointStatus.None;
        job.GitCheckpointBranch = null;
        job.GitCheckpointBaseBranch = null;
        job.GitCheckpointCommitHash = null;
        job.GitCheckpointReason = null;
        job.GitCheckpointCapturedAt = null;
        // Keep SessionId for potential session continuation
        // Keep InputTokens, OutputTokens, TotalCostUsd for historical tracking
        // Keep Planning/Execution stage stats for historical tracking
        // Keep Messages for audit trail and context

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about status change
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return true;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job != null)
        {
            var projectId = job.ProjectId;
            _dbContext.Jobs.Remove(job);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Notify about job deletion
            if (_jobUpdateService != null)
            {
                try
                {
                    await _jobUpdateService.NotifyJobDeleted(id, projectId);
                }
                catch { }
            }
        }
    }

    public async Task<bool> ForceFailJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null || JobStateMachine.IsTerminalState(job.Status))
        {
            return false;
        }

        job.Status = JobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        job.ErrorMessage = "Manually marked as failed by user.";
        job.CurrentActivity = null;
        job.WorkerInstanceId = null;
        job.ProcessId = null;
        job.LastHeartbeatAt = null;
        job.CancellationRequested = false;

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

    public async Task<bool> UpdateJobPromptAsync(Guid id, string newPrompt, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        // Only allow updating prompt for jobs that haven't started yet or are in terminal states
        if (job.Status != JobStatus.New && job.Status != JobStatus.Failed && job.Status != JobStatus.Cancelled)
        {
            return false;
        }

        var shouldSyncTitle = JobTitleHelper.ShouldSyncTitleWithGoalPrompt(job.Title, job.GoalPrompt);
        job.GoalPrompt = newPrompt?.Trim() ?? string.Empty;
        job.Title = shouldSyncTitle
            ? JobTitleHelper.BuildSafeJobTitle(null, job.GoalPrompt)
            : JobTitleHelper.BuildSafeJobTitle(job.Title, job.GoalPrompt);
        job.PlanningOutput = null;
        job.PlanningProviderId = null;
        job.PlanningModelUsed = null;
        job.PlanningReasoningEffortUsed = null;
        job.PlanningGeneratedAt = null;
        job.PlanningInputTokens = null;
        job.PlanningOutputTokens = null;
        job.PlanningCostUsd = null;
        job.ExecutionInputTokens = null;
        job.ExecutionOutputTokens = null;
        job.ExecutionCostUsd = null;
        job.CommandUsed = null;
        job.PlanningCommandUsed = null;
        job.ExecutionCommandUsed = null;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<int> CancelAllByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var activeStatuses = new[]
        {
            JobStatus.New, JobStatus.Pending, JobStatus.Started, JobStatus.Planning,
            JobStatus.Processing, JobStatus.Paused, JobStatus.Stalled
        };

        var jobs = await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId && activeStatuses.Contains(j.Status))
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        foreach (var job in jobs)
        {
            // Kill running processes
            if (job.ProcessId.HasValue && (job.Status == JobStatus.Started || job.Status == JobStatus.Planning || job.Status == JobStatus.Processing))
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(job.ProcessId.Value);
                    process.Kill(entireProcessTree: true);
                }
                catch { /* Process already exited or inaccessible */ }
            }

            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.CancellationRequested = true;
            job.CurrentActivity = null;
            job.WorkerInstanceId = null;
            job.ProcessId = null;
            job.ErrorMessage ??= "Cancelled in bulk by user.";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about cancellations
        if (_jobUpdateService != null)
        {
            foreach (var job in jobs)
            {
                try
                {
                    await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                }
                catch { }
            }
        }

        _jobProcessingService?.TriggerProcessing();

        return jobs.Count;
    }

    public async Task<int> DeleteCompletedByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var jobs = await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId && j.Status == JobStatus.Completed)
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return 0;
        }

        var deletedJobs = jobs
            .Select(job => new { job.Id, job.ProjectId })
            .ToList();

        _dbContext.Jobs.RemoveRange(jobs);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_jobUpdateService != null)
        {
            foreach (var job in deletedJobs)
            {
                try
                {
                    await _jobUpdateService.NotifyJobDeleted(job.Id, job.ProjectId);
                }
                catch { }
            }
        }

        return deletedJobs.Count;
    }

    public async Task<int> CancelSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default)
    {
        if (jobIds.Count == 0)
        {
            return 0;
        }

        var activeStatuses = new[]
        {
            JobStatus.New, JobStatus.Pending, JobStatus.Started, JobStatus.Planning,
            JobStatus.Processing, JobStatus.Paused, JobStatus.Stalled
        };

        var jobs = await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId && jobIds.Contains(j.Id) && activeStatuses.Contains(j.Status))
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        foreach (var job in jobs)
        {
            if (job.ProcessId.HasValue && (job.Status == JobStatus.Started || job.Status == JobStatus.Planning || job.Status == JobStatus.Processing))
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(job.ProcessId.Value);
                    process.Kill(entireProcessTree: true);
                }
                catch { }
            }

            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.CancellationRequested = true;
            job.CurrentActivity = null;
            job.WorkerInstanceId = null;
            job.ProcessId = null;
            job.ErrorMessage ??= "Cancelled in bulk by user.";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_jobUpdateService != null)
        {
            foreach (var job in jobs)
            {
                try
                {
                    await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());
                }
                catch { }
            }

            try
            {
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return jobs.Count;
    }

    public async Task<int> PrioritizeSelectedByProjectIdAsync(Guid projectId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken = default)
    {
        if (jobIds.Count == 0)
        {
            return 0;
        }

        var jobs = await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId && jobIds.Contains(j.Id) && j.Status == JobStatus.New && !j.CancellationRequested)
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return 0;
        }

        var maxPriority = await _dbContext.Jobs
            .Where(j => j.ProjectId == projectId)
            .Select(j => (int?)j.Priority)
            .MaxAsync(cancellationToken) ?? 0;

        foreach (var job in jobs)
        {
            maxPriority++;
            job.Priority = maxPriority;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobListChanged();
            }
            catch { }
        }

        _jobProcessingService?.TriggerProcessing();

        return jobs.Count;
    }
}
