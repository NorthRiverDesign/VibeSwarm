using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl.Models;
using VibeSwarm.Shared;

namespace VibeSwarm.Web.Services;

public partial class JobProcessingService
{
    private async Task<(string? GitDiff, IReadOnlyList<string>? CommitLog)> CaptureGitDiffWithRetryAsync(
        string workingDirectory, string? baseCommit,
        CancellationToken cancellationToken, int maxAttempts = 2)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string? gitDiff = null;
            IReadOnlyList<string>? commitLog = null;

            if (!string.IsNullOrEmpty(baseCommit))
            {
                var committedDiff = await _versionControlService.GetCommitRangeDiffAsync(workingDirectory, baseCommit, null, cancellationToken);

                commitLog = await _versionControlService.GetCommitLogAsync(workingDirectory, baseCommit, null, cancellationToken);
                if (commitLog.Count > 0)
                {
                    _logger.LogInformation("Found {Count} commits since base commit {BaseCommit}", commitLog.Count, baseCommit);
                }

                var uncommittedDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, null, cancellationToken);

                if (!string.IsNullOrEmpty(committedDiff) && !string.IsNullOrEmpty(uncommittedDiff))
                {
                    gitDiff = $"=== Committed changes since {baseCommit} ===\n{committedDiff}\n\n=== Uncommitted changes ===\n{uncommittedDiff}";
                }
                else if (!string.IsNullOrEmpty(committedDiff))
                {
                    gitDiff = committedDiff;
                }
                else if (!string.IsNullOrEmpty(uncommittedDiff))
                {
                    gitDiff = uncommittedDiff;
                }

                // Fallback: if both were empty, try a single diff from baseCommit against working tree
                if (string.IsNullOrEmpty(gitDiff))
                {
                    _logger.LogInformation("Committed and uncommitted diffs both empty, trying fallback diff from {BaseCommit} against working tree (attempt {Attempt}/{Max})",
                        baseCommit, attempt, maxAttempts);
                    gitDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, baseCommit, cancellationToken);
                }
            }
            else
            {
                gitDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, null, cancellationToken);
            }

            if (!string.IsNullOrEmpty(gitDiff))
            {
                return (gitDiff, commitLog);
            }

            if (attempt < maxAttempts)
            {
                _logger.LogInformation("Git diff capture returned empty on attempt {Attempt}/{Max}, retrying after delay", attempt, maxAttempts);
                await Task.Delay(1000, cancellationToken);
            }
        }

        return (null, null);
    }

    private async Task PrepareWorkingBranchAsync(Job job, string workingDirectory, string? checkpointBaseBranch, CancellationToken cancellationToken)
    {
        var sourceBranch = string.IsNullOrWhiteSpace(job.Branch)
            ? (string.IsNullOrWhiteSpace(checkpointBaseBranch) ? null : checkpointBaseBranch.Trim())
            : job.Branch.Trim();
        var targetBranch = GetEffectiveTargetBranch(job);

        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            _logger.LogInformation("Syncing current branch before job {JobId} execution", job.Id);
            var syncResult = await _versionControlService.SyncWithOriginAsync(workingDirectory, cancellationToken: cancellationToken);
            if (!syncResult.Success)
            {
                _logger.LogWarning("Failed to sync current branch before job {JobId}: {Error}", job.Id, syncResult.Error);
            }
            return;
        }

        var branches = await _versionControlService.GetBranchesAsync(workingDirectory, includeRemote: true, cancellationToken);
        var sourceExists = BranchExists(branches, sourceBranch);

        if (sourceExists)
        {
            _logger.LogInformation("Checking out configured branch '{Branch}' for job {JobId}", sourceBranch, job.Id);
            var checkoutResult = await _versionControlService.HardCheckoutBranchAsync(workingDirectory, sourceBranch, cancellationToken: cancellationToken);
            if (!checkoutResult.Success)
            {
                _logger.LogWarning("Failed to checkout branch '{Branch}' for job {JobId}: {Error}", sourceBranch, job.Id, checkoutResult.Error);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(targetBranch) &&
            !string.Equals(targetBranch, sourceBranch, StringComparison.Ordinal) &&
            BranchExists(branches, targetBranch))
        {
            _logger.LogInformation("Using target branch '{TargetBranch}' as the base for new branch '{SourceBranch}' on job {JobId}", targetBranch, sourceBranch, job.Id);
            var baseCheckoutResult = await _versionControlService.HardCheckoutBranchAsync(workingDirectory, targetBranch, cancellationToken: cancellationToken);
            if (!baseCheckoutResult.Success)
            {
                _logger.LogWarning("Failed to checkout target branch '{Branch}' for job {JobId}: {Error}", targetBranch, job.Id, baseCheckoutResult.Error);
            }
        }
        else
        {
            var syncResult = await _versionControlService.SyncWithOriginAsync(workingDirectory, cancellationToken: cancellationToken);
            if (!syncResult.Success)
            {
                _logger.LogWarning("Failed to sync current branch before creating new branch '{Branch}' for job {JobId}: {Error}", sourceBranch, job.Id, syncResult.Error);
            }
        }

        var createResult = await _versionControlService.CreateBranchAsync(
            workingDirectory,
            sourceBranch,
            switchToBranch: true,
            cancellationToken: cancellationToken);
        if (!createResult.Success)
        {
            _logger.LogWarning("Failed to create job branch '{Branch}' for job {JobId}: {Error}", sourceBranch, job.Id, createResult.Error);
        }
    }

    private async Task<string?> PreserveWorkingTreeBeforeBranchPreparationAsync(
        Job job,
        string workingDirectory,
        VibeSwarmDbContext dbContext,
        bool captureJobDiff,
        string reason,
        CancellationToken cancellationToken)
    {
        var hasUncommittedChanges = await _versionControlService.HasUncommittedChangesAsync(workingDirectory, cancellationToken);
        if (!hasUncommittedChanges)
        {
            return null;
        }

        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Protecting);

        var originalBranch = await _versionControlService.GetCurrentBranchAsync(workingDirectory, cancellationToken);
        if (captureJobDiff)
        {
            job.GitDiff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, null, cancellationToken);
            var changedFiles = await _versionControlService.GetChangedFilesAsync(workingDirectory, null, cancellationToken);
            job.ChangedFilesCount = changedFiles.Count;
        }

        var recoveryBranch = BuildRecoveryBranchName(job.Id, originalBranch);
        var createBranchResult = await _versionControlService.CreateBranchAsync(
            workingDirectory,
            recoveryBranch,
            switchToBranch: true,
            cancellationToken: cancellationToken);

        if (!createBranchResult.Success)
        {
            job.GitCheckpointStatus = GitCheckpointStatus.None;
            throw new GitCheckpointRequiredException($"Unable to preserve local git changes before branch preparation: {createBranchResult.Error}");
        }

        var checkpointMessage = $"{AppConstants.AppName} checkpoint before job {job.Id.ToString("N")[..8]}";
        var commitMessage = string.IsNullOrWhiteSpace(originalBranch)
            ? checkpointMessage
            : $"{checkpointMessage} on {originalBranch}";
        var commitResult = await _versionControlService.CommitAllChangesAsync(workingDirectory, commitMessage, cancellationToken);
        if (!commitResult.Success)
        {
            job.GitCheckpointStatus = GitCheckpointStatus.None;
            throw new GitCheckpointRequiredException($"Unable to commit preserved local git changes before branch preparation: {commitResult.Error}");
        }

        job.GitCheckpointBranch = recoveryBranch;
        job.GitCheckpointBaseBranch = originalBranch;
        job.GitCheckpointCommitHash = commitResult.CommitHash;
        job.GitCheckpointReason = reason;
        job.GitCheckpointCapturedAt = DateTime.UtcNow;
        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Preserved);

        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Preserved local git changes for job {JobId} on recovery branch {RecoveryBranch} ({CommitHash}) before branch preparation",
            job.Id,
            recoveryBranch,
            commitResult.CommitHash?[..Math.Min(8, commitResult.CommitHash?.Length ?? 0)]);

        return originalBranch;
    }

    private async Task TryRecordAgentCommitAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(job.GitCommitHash))
        {
            return;
        }

        var hasChanges = await _versionControlService.HasUncommittedChangesAsync(workingDirectory, cancellationToken);
        if (hasChanges)
        {
            return;
        }

        var currentHash = await _versionControlService.GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(currentHash) ||
            string.Equals(currentHash, job.GitCommitBefore, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        job.GitCommitHash = currentHash;
        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Cleared);
        _logger.LogInformation(
            "Recorded self-committed agent output for job {JobId} at {CommitHash}",
            job.Id,
            currentHash[..Math.Min(8, currentHash.Length)]);
    }

    private static string BuildRecoveryBranchName(Guid jobId, string? originalBranch)
    {
        var branchSlug = string.IsNullOrWhiteSpace(originalBranch) ? "detached" : SanitizeBranchSegment(originalBranch);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return $"vibeswarm/recovery/{branchSlug}-{timestamp}-{jobId.ToString("N")[..8]}";
    }

    private static string SanitizeBranchSegment(string branchName)
    {
        var sanitized = Regex.Replace(branchName.Trim().ToLowerInvariant(), @"[^a-z0-9/_-]+", "-");
        sanitized = sanitized.Replace("//", "/").Trim('-', '/');
        return string.IsNullOrWhiteSpace(sanitized) ? "branch" : sanitized;
    }

    private async Task<bool> TryPreserveChangesForRecoveryAsync(Job job, string reason, CancellationToken cancellationToken)
    {
        var workingDirectory = job.Project?.WorkingPath;
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return false;
        }

        if (!await _versionControlService.IsGitRepositoryAsync(workingDirectory, cancellationToken))
        {
            return false;
        }

        var workingTreeStatus = await _versionControlService.GetWorkingTreeStatusAsync(workingDirectory, cancellationToken);
        if (!workingTreeStatus.HasUncommittedChanges)
        {
            return false;
        }

        var diff = await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, job.GitCommitBefore, cancellationToken)
            ?? await _versionControlService.GetWorkingDirectoryDiffAsync(workingDirectory, cancellationToken: cancellationToken);

        var preserveResult = await _versionControlService.PreserveChangesAsync(
            workingDirectory,
            $"{AppConstants.AppName} job {job.Id}: {reason}",
            cancellationToken);

        if (!preserveResult.Success)
        {
            _logger.LogWarning("Failed to preserve workspace changes for recovered job {JobId}: {Error}", job.Id, preserveResult.Error);
            return false;
        }

        var transition = JobStateMachine.TryTransition(job, JobStatus.Stalled, reason);
        if (!transition.Success)
        {
            _logger.LogWarning("Failed to move recovered job {JobId} into stalled state: {Error}", job.Id, transition.ErrorMessage);
            return false;
        }

        job.GitDiff = !string.IsNullOrWhiteSpace(diff) ? diff : job.GitDiff;
        job.ChangedFilesCount = workingTreeStatus.ChangedFilesCount;
        job.WorkerInstanceId = null;
        job.LastHeartbeatAt = null;
        job.ProcessId = null;
        job.CurrentActivity = null;
        job.ErrorMessage = $"{reason} Preserved {workingTreeStatus.ChangedFilesCount} changed file(s) in {preserveResult.SavedReference ?? "stash@{0}"} for recovery.";

        return true;
    }
}
