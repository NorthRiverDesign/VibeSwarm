using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Pauses between fetch attempts before a job gives up on the remote and waits.
    /// </summary>
    internal TimeSpan[] FetchRetryDelays { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    private static readonly TimeSpan GitRemoteRetryDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Puts the checkout on the job's branch at the latest commit on origin. Anything that would let
    /// the agent start on the wrong branch or on stale code stops the job before it runs: an
    /// unreachable remote re-queues it (<see cref="GitRemoteUnavailableException"/>), anything else
    /// fails it (<see cref="GitPreparationException"/>).
    /// </summary>
    private async Task PrepareWorkingBranchAsync(Job job, string workingDirectory, string? checkpointBaseBranch, CancellationToken cancellationToken)
    {
        var sourceBranch = string.IsNullOrWhiteSpace(job.Branch)
            ? (string.IsNullOrWhiteSpace(checkpointBaseBranch) ? null : checkpointBaseBranch.Trim())
            : job.Branch.Trim();
        var targetBranch = GetEffectiveTargetBranch(job);

        if (sourceBranch == null)
        {
            var currentBranch = await _versionControlService.GetCurrentBranchAsync(workingDirectory, cancellationToken);
            if (IsRecoveryBranch(currentBranch))
            {
                // Older releases left the checkout here after a cancelled run. A job started on it
                // would deliver its work to the recovery branch instead of the project's branch.
                sourceBranch = await FindRecoveryBaseBranchAsync(workingDirectory, currentBranch!, cancellationToken)
                    ?? throw new GitPreparationException(
                        $"The project checkout is on the recovery branch '{currentBranch}'. Switch it back to the project's working branch, then retry the job.");
            }
        }

        var hasRemote = !string.IsNullOrWhiteSpace(
            await _versionControlService.GetRemoteUrlAsync(workingDirectory, cancellationToken: cancellationToken));
        if (hasRemote)
        {
            await FetchWithRetryAsync(job, workingDirectory, cancellationToken);
        }

        GitOperationResult? syncResult = null;
        if (sourceBranch == null)
        {
            if (hasRemote)
            {
                _logger.LogInformation("Syncing current branch before job {JobId} execution", job.Id);
                syncResult = await _versionControlService.SyncWithOriginAsync(workingDirectory, cancellationToken: cancellationToken);
                EnsureSynced(syncResult, "the current branch");
            }

            LogKeptLocalCommits(job, syncResult);
            return;
        }

        var branches = await _versionControlService.GetBranchesAsync(workingDirectory, includeRemote: true, cancellationToken);
        if (BranchExists(branches, sourceBranch))
        {
            _logger.LogInformation("Checking out configured branch '{Branch}' for job {JobId}", sourceBranch, job.Id);
            syncResult = await CheckOutLatestAsync(workingDirectory, sourceBranch, hasRemote, cancellationToken);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(targetBranch) &&
                !string.Equals(targetBranch, sourceBranch, StringComparison.Ordinal) &&
                BranchExists(branches, targetBranch))
            {
                _logger.LogInformation("Using target branch '{TargetBranch}' as the base for new branch '{SourceBranch}' on job {JobId}", targetBranch, sourceBranch, job.Id);
                syncResult = await CheckOutLatestAsync(workingDirectory, targetBranch, hasRemote, cancellationToken);
            }
            else if (hasRemote)
            {
                syncResult = await _versionControlService.SyncWithOriginAsync(workingDirectory, cancellationToken: cancellationToken);
                EnsureSynced(syncResult, "the current branch");
            }

            var createResult = await _versionControlService.CreateBranchAsync(
                workingDirectory,
                sourceBranch,
                switchToBranch: true,
                cancellationToken: cancellationToken);
            if (!createResult.Success)
            {
                throw new GitPreparationException($"Couldn't create the job branch '{sourceBranch}': {createResult.Error}");
            }
        }

        var checkedOut = await _versionControlService.GetCurrentBranchAsync(workingDirectory, cancellationToken);
        if (!string.Equals(checkedOut, sourceBranch, StringComparison.Ordinal))
        {
            throw new GitPreparationException($"Expected the checkout to be on '{sourceBranch}' before the job, but it is on '{checkedOut ?? "no branch"}'.");
        }

        LogKeptLocalCommits(job, syncResult);
    }

    private async Task<GitOperationResult> CheckOutLatestAsync(string workingDirectory, string branch, bool hasRemote, CancellationToken cancellationToken)
    {
        var result = hasRemote
            ? await _versionControlService.HardCheckoutBranchAsync(workingDirectory, branch, cancellationToken: cancellationToken)
            : await _versionControlService.SwitchBranchAsync(workingDirectory, branch, cancellationToken);
        if (!result.Success)
        {
            throw new GitPreparationException($"Couldn't check out the latest '{branch}' before the job: {result.Error}");
        }

        return result;
    }

    /// <summary>
    /// A branch that only exists locally has nothing to pull; any other sync failure leaves the
    /// checkout in a state the job must not start from.
    /// </summary>
    private static void EnsureSynced(GitOperationResult result, string description)
    {
        if (!result.Success &&
            result.Error?.Contains("Remote tracking branch", StringComparison.Ordinal) != true)
        {
            throw new GitPreparationException($"Couldn't bring {description} up to date with origin before the job: {result.Error}");
        }
    }

    private async Task FetchWithRetryAsync(Job job, string workingDirectory, CancellationToken cancellationToken)
    {
        GitOperationResult? fetch = null;
        for (var attempt = 0; attempt <= FetchRetryDelays.Length; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(FetchRetryDelays[attempt - 1], cancellationToken);
            }

            fetch = await _versionControlService.FetchAsync(workingDirectory, cancellationToken: cancellationToken);
            if (fetch.Success)
            {
                return;
            }

            _logger.LogWarning("Fetch before job {JobId} failed (attempt {Attempt}): {Error}", job.Id, attempt + 1, fetch.Error);
        }

        throw new GitRemoteUnavailableException($"Couldn't pull the latest changes from origin: {fetch?.Error}");
    }

    private void LogKeptLocalCommits(Job job, GitOperationResult? syncResult)
    {
        if (syncResult?.RecoveryBranch != null)
        {
            _logger.LogWarning(
                "Unpushed commits conflicted with origin before job {JobId}; they are kept on {RecoveryBranch}",
                job.Id,
                syncResult.RecoveryBranch);
        }
        else if (syncResult?.KeptLocalCommits > 0)
        {
            _logger.LogInformation(
                "Kept {Count} unpushed commit(s) on top of origin before job {JobId}",
                syncResult.KeptLocalCommits,
                job.Id);
        }
    }

    private static bool IsRecoveryBranch(string? branch) =>
        branch?.StartsWith(RecoveryBranchPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Recovery branches are named <c>vibeswarm/recovery/{base}-{timestamp}-{suffix}</c>. Returns
    /// the base branch when one by that exact name exists.
    /// </summary>
    private async Task<string?> FindRecoveryBaseBranchAsync(string workingDirectory, string recoveryBranch, CancellationToken cancellationToken)
    {
        var match = RecoveryBranchPattern().Match(recoveryBranch);
        if (!match.Success)
        {
            return null;
        }

        var baseBranch = match.Groups["base"].Value;
        var branches = await _versionControlService.GetBranchesAsync(workingDirectory, includeRemote: true, cancellationToken);
        return BranchExists(branches, baseBranch) ? baseBranch : null;
    }

    [GeneratedRegex(@"^vibeswarm/recovery/(?<base>.+)-\d{8}-\d{6}-[0-9a-z]+$")]
    private static partial Regex RecoveryBranchPattern();

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
        var originalCommit = await _versionControlService.GetCurrentCommitHashAsync(workingDirectory, cancellationToken);
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
            throw new GitPreparationException($"Unable to preserve local git changes before branch preparation: {createBranchResult.Error}");
        }

        var checkpointMessage = $"{AppConstants.AppName} checkpoint before job {job.Id.ToString("N")[..8]}";
        var commitMessage = string.IsNullOrWhiteSpace(originalBranch)
            ? checkpointMessage
            : $"{checkpointMessage} on {originalBranch}";
        var commitResult = await _versionControlService.CommitAllChangesAsync(workingDirectory, commitMessage, cancellationToken);
        if (!commitResult.Success)
        {
            job.GitCheckpointStatus = GitCheckpointStatus.None;
            throw new GitPreparationException($"Unable to commit preserved local git changes before branch preparation: {commitResult.Error}");
        }

        job.GitCheckpointBranch = recoveryBranch;
        job.GitCheckpointBaseBranch = originalBranch;
        job.GitCheckpointCommitHash = commitResult.CommitHash;
        job.GitCheckpointReason = reason;
        job.GitCheckpointCapturedAt = DateTime.UtcNow;
        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Preserved);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Go back to where the checkout was. Left on the recovery branch, the next job would run
        // there and deliver its work to it instead of to the project's branch.
        var returnTo = string.IsNullOrWhiteSpace(originalBranch) || originalBranch == "HEAD" ? originalCommit : originalBranch;
        if (!string.IsNullOrWhiteSpace(returnTo))
        {
            var switchBack = await _versionControlService.SwitchBranchAsync(workingDirectory, returnTo, cancellationToken);
            if (!switchBack.Success)
            {
                throw new GitPreparationException($"Preserved local changes on {recoveryBranch}, but couldn't switch back to {returnTo}: {switchBack.Error}");
            }
        }

        _logger.LogWarning(
            "Preserved local git changes for job {JobId} on recovery branch {RecoveryBranch} ({CommitHash}) before branch preparation",
            job.Id,
            recoveryBranch,
            commitResult.CommitHash?[..Math.Min(8, commitResult.CommitHash?.Length ?? 0)]);

        return originalBranch;
    }

    /// <summary>
    /// Puts the work of the job's earlier runs back on top of the freshly prepared branch, so a
    /// follow-up builds on it and delivers it along with its own changes. Runs are checked newest
    /// first: one whose commit is already on the branch means everything before it is there too.
    /// </summary>
    private async Task<JobWorkRestoreResult?> RestorePriorRunWorkAsync(
        Job job,
        string workingDirectory,
        VibeSwarmDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (_workSnapshots == null)
        {
            return null;
        }

        // A run resumed after an interruption gets back exactly what it had done. That snapshot
        // already holds whatever earlier runs restored, so nothing older is needed.
        if (JobRecoveryHelper.IsResumeCandidate(job) && !string.IsNullOrWhiteSpace(job.WorkSnapshotCommit))
        {
            const string resumeActivity = "Restoring the work from before the interruption...";
            await UpdateHeartbeatAsync(job.Id, resumeActivity, dbContext, cancellationToken);
            await NotifyJobActivityAsync(job.Id, resumeActivity, DateTime.UtcNow);
            var restored = await _workSnapshots.RestoreAsync(workingDirectory, job.WorkSnapshotCommit, cancellationToken);
            _logger.LogInformation("Restored the interrupted work {Snapshot} for job {JobId}: {Outcome}",
                job.WorkSnapshotCommit, job.Id, restored.Outcome);
            return restored;
        }

        var priorRuns = await dbContext.JobChangeSets
            .AsNoTracking()
            .Where(cs => cs.JobId == job.Id)
            .OrderByDescending(cs => cs.FollowUpIndex)
            .Select(cs => new { cs.GitCommitHash, cs.WorkSnapshotCommit })
            .ToListAsync(cancellationToken);

        try
        {
            foreach (var run in priorRuns)
            {
                if (!string.IsNullOrWhiteSpace(run.GitCommitHash) &&
                    await _workSnapshots.IsInHeadAsync(workingDirectory, run.GitCommitHash, cancellationToken))
                {
                    return JobWorkRestoreResult.AlreadyOnBranch();
                }

                if (string.IsNullOrWhiteSpace(run.WorkSnapshotCommit))
                {
                    continue;
                }

                const string activity = "Restoring changes from earlier runs...";
                await UpdateHeartbeatAsync(job.Id, activity, dbContext, cancellationToken);
                await NotifyJobActivityAsync(job.Id, activity, DateTime.UtcNow);

                var result = await _workSnapshots.RestoreAsync(workingDirectory, run.WorkSnapshotCommit, cancellationToken);
                if (result.Outcome == JobWorkRestoreOutcome.Failed)
                {
                    _logger.LogWarning("Could not restore earlier work {Snapshot} for job {JobId}: {Error}",
                        run.WorkSnapshotCommit, job.Id, result.Error);
                }
                else
                {
                    _logger.LogInformation("Restored earlier work {Snapshot} for job {JobId}: {Outcome}, {FileCount} file(s), {ConflictCount} conflicted",
                        run.WorkSnapshotCommit, job.Id, result.Outcome, result.Files.Count, result.ConflictedFiles.Count);
                }

                return result;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not restore earlier work for job {JobId}", job.Id);
            return JobWorkRestoreResult.Failed(ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Saves what the run leaves behind so a follow-up can restore it after the next pre-run reset.
    /// </summary>
    private async Task<string?> SaveRunWorkSnapshotAsync(Job job, string workingDirectory, string? baseCommit, CancellationToken cancellationToken)
    {
        if (_workSnapshots == null || string.IsNullOrWhiteSpace(baseCommit))
        {
            return null;
        }

        try
        {
            var snapshot = await _workSnapshots.SaveAsync(workingDirectory, job.Id, baseCommit, cancellationToken);
            if (snapshot != null)
            {
                _logger.LogInformation("Saved the work of job {JobId} as {Snapshot}", job.Id, snapshot[..Math.Min(8, snapshot.Length)]);
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not save the work of job {JobId}", job.Id);
            return null;
        }
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

    /// <summary>
    /// Brings home the work of any worktree the agent created and left behind. When the checkout
    /// itself is untouched, the worktree's changes become the job's changes; otherwise they stay
    /// on a branch and the job says which.
    /// </summary>
    private async Task CollectLeftoverWorktreesAsync(Job job, string? workingDirectory, JobExecutionContext executionContext)
    {
        if (_workSnapshots == null || string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        try
        {
            var checkout = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var leftovers = (await _workSnapshots.ListWorktreesAsync(workingDirectory, CancellationToken.None))
                .Where(path => !executionContext.WorktreesBefore.Contains(path, StringComparer.Ordinal))
                .Where(path => !string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), checkout, StringComparison.Ordinal))
                .ToList();

            // Remove them all first: a worktree inside the repository would make the checkout
            // itself look changed.
            var keptBranches = new List<string>();
            foreach (var worktree in leftovers)
            {
                if (await _workSnapshots.CollectWorktreeAsync(workingDirectory, worktree, job.Id, CancellationToken.None) is { } branch)
                {
                    keptBranches.Add(branch);
                }
            }

            foreach (var branch in keptBranches)
            {
                var checkoutUntouched =
                    !await _versionControlService.HasUncommittedChangesAsync(workingDirectory, CancellationToken.None) &&
                    string.Equals(
                        await _versionControlService.GetCurrentCommitHashAsync(workingDirectory, CancellationToken.None),
                        executionContext.GitCommitBefore,
                        StringComparison.OrdinalIgnoreCase);
                if (checkoutUntouched && await _workSnapshots.MergeIntoCleanCheckoutAsync(workingDirectory, branch, CancellationToken.None))
                {
                    _logger.LogInformation("Brought the work job {JobId} did in a worktree into the checkout", job.Id);
                    continue;
                }

                executionContext.DeliveryNotice = CombineNotices(
                    executionContext.DeliveryNotice,
                    $"The agent left work in a git worktree that could not be merged into the checkout. It is kept on the branch {branch}.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check for worktrees left by job {JobId}", job.Id);
        }
    }

    private static string? CombineNotices(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? second
        : string.IsNullOrWhiteSpace(second) ? first
        : $"{first} {second}";

    private const string RecoveryBranchPrefix = "vibeswarm/recovery/";

    private static string BuildRecoveryBranchName(Guid jobId, string? originalBranch)
    {
        var branchSlug = string.IsNullOrWhiteSpace(originalBranch) ? "detached" : SanitizeBranchSegment(originalBranch);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return $"{RecoveryBranchPrefix}{branchSlug}-{timestamp}-{jobId.ToString("N")[..8]}";
    }

    private static string SanitizeBranchSegment(string branchName)
    {
        var sanitized = Regex.Replace(branchName.Trim().ToLowerInvariant(), @"[^a-z0-9/_-]+", "-");
        sanitized = sanitized.Replace("//", "/").Trim('-', '/');
        return string.IsNullOrWhiteSpace(sanitized) ? "branch" : sanitized;
    }
}
