using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Shared.Services;

public partial class JobService
{
    public async Task<bool> UpdateGitCommitHashAsync(Guid id, string commitHash, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        job.GitCommitHash = commitHash;
        JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Cleared);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> UpdateGitDiffAsync(Guid id, string? gitDiff, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        job.GitDiff = gitDiff;
        job.ChangedFilesCount = string.IsNullOrWhiteSpace(gitDiff)
            ? 0
            : GitDiffParser.ParseDiff(gitDiff).Count;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify about git diff update
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobGitDiffUpdated(job.Id, !string.IsNullOrEmpty(gitDiff));
            }
            catch { }
        }

        return true;
    }

    public async Task<bool> UpdateGitDeliveryAsync(
        Guid id,
        string? commitHash = null,
        int? pullRequestNumber = null,
        string? pullRequestUrl = null,
        DateTime? pullRequestCreatedAt = null,
        DateTime? mergedAt = null,
        CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(commitHash))
        {
            job.GitCommitHash = commitHash.Trim();
            JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Cleared);
        }

        if (pullRequestNumber.HasValue)
        {
            job.PullRequestNumber = pullRequestNumber;
        }

        if (!string.IsNullOrWhiteSpace(pullRequestUrl))
        {
            job.PullRequestUrl = pullRequestUrl.Trim();
        }

        if (pullRequestCreatedAt.HasValue)
        {
            job.PullRequestCreatedAt = pullRequestCreatedAt;
        }

        if (mergedAt.HasValue)
        {
            job.MergedAt = mergedAt;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
