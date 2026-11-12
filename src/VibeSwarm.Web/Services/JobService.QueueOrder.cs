using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// A queued job's place in the queue: why it has not started yet, and moving it to the front.
/// The queue starts jobs by priority (highest first), then oldest first, one at a time per
/// project and per provider (see <see cref="GetPendingJobsAsync"/>).
/// </summary>
public partial class JobService
{
	private const int QueueStatusTitleMaxLength = 120;

	/// <summary>Statuses that hold a project's checkout and a provider, as the scheduler counts them.</summary>
	private static readonly Expression<Func<Job, bool>> HoldsSlot = j =>
		j.Status == JobStatus.Pending
		|| j.Status == JobStatus.Started
		|| j.Status == JobStatus.Planning
		|| j.Status == JobStatus.Processing
		|| j.Status == JobStatus.Paused
		|| j.Status == JobStatus.Stalled;

	public async Task<bool> RunNextAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var job = await _dbContext.Jobs
			.FirstOrDefaultAsync(j => j.Id == id && j.Status == JobStatus.New && !j.CancellationRequested, cancellationToken);
		if (job == null)
		{
			return false;
		}

		// Ahead of every queued job in every project, since a job usually waits on a provider
		// that other projects' jobs are queued for too.
		var highestOtherPriority = await _dbContext.Jobs
			.Where(j => j.Id != id && j.Status == JobStatus.New && !j.CancellationRequested)
			.Select(j => (int?)j.Priority)
			.MaxAsync(cancellationToken);

		if (highestOtherPriority.HasValue && job.Priority <= highestOtherPriority.Value)
		{
			job.Priority = highestOtherPriority.Value + 1;
			await _dbContext.SaveChangesAsync(cancellationToken);

			if (_jobUpdateService != null)
			{
				try
				{
					await _jobUpdateService.NotifyJobListChanged();
				}
				catch { }
			}
		}

		_jobProcessingService?.TriggerProcessing();
		return true;
	}

	public async Task<JobQueueStatus?> GetQueueStatusAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var job = await _dbContext.Jobs
			.AsNoTracking()
			.Where(j => j.Id == id && j.Status == JobStatus.New)
			.Select(j => new
			{
				j.ProjectId,
				j.ProviderId,
				ProviderName = j.Provider != null ? j.Provider.Name : null,
				j.SwarmId,
				j.Priority,
				j.CreatedAt,
				j.NotBeforeUtc,
				j.DependsOnJobId
			})
			.FirstOrDefaultAsync(cancellationToken);
		if (job == null)
		{
			return null;
		}

		var now = DateTime.UtcNow;
		var hasProvider = job.ProviderId != Guid.Empty;
		var status = new JobQueueStatus { ProviderName = job.ProviderName };

		// Queued jobs that start first when this one's project or provider frees up.
		status.JobsAhead = await _dbContext.Jobs
			.Where(j => j.Id != id && j.Status == JobStatus.New && !j.CancellationRequested)
			.Where(j => j.NotBeforeUtc == null || j.NotBeforeUtc <= now)
			.Where(j => j.Priority > job.Priority || (j.Priority == job.Priority && j.CreatedAt < job.CreatedAt))
			.Where(j => (j.ProjectId == job.ProjectId && (job.SwarmId == null || j.SwarmId != job.SwarmId))
				|| (hasProvider && j.ProviderId == job.ProviderId))
			.CountAsync(cancellationToken);

		var queuePaused = await _dbContext.AppSettings
			.AsNoTracking()
			.Select(settings => (bool?)settings.JobQueuePaused)
			.FirstOrDefaultAsync(cancellationToken) ?? false;
		if (queuePaused)
		{
			status.Reason = JobQueueWaitReason.QueuePaused;
			return status;
		}

		if (job.NotBeforeUtc > now)
		{
			status.Reason = JobQueueWaitReason.HeldUntil;
			status.WaitUntilUtc = job.NotBeforeUtc;
			return status;
		}

		if (job.DependsOnJobId is { } dependencyId)
		{
			var dependency = await GetQueueBlockerAsync(_dbContext.Jobs.Where(j => j.Id == dependencyId), cancellationToken);
			if (dependency?.Status != JobStatus.Completed)
			{
				status.Reason = JobQueueWaitReason.WaitingOnDependency;
				ApplyBlocker(status, dependency);
				return status;
			}
		}

		if (_serviceProvider.GetService<ProjectQueueHolds>()?.GetHeldUntil(job.ProjectId) is { } heldUntil)
		{
			status.Reason = JobQueueWaitReason.ProjectEditHold;
			status.WaitUntilUtc = heldUntil;
			return status;
		}

		var slotHolders = _dbContext.Jobs.Where(HoldsSlot).Where(j => j.Id != id);
		var projectHolders = await slotHolders
			.Where(j => j.ProjectId == job.ProjectId)
			.Select(j => new { j.SwarmId })
			.ToListAsync(cancellationToken);
		var projectSlots = new ProjectJobSlots(projectHolders.Select(holder => (job.ProjectId, holder.SwarmId)));
		if (!projectSlots.CanStart(job.ProjectId, job.SwarmId))
		{
			status.Reason = JobQueueWaitReason.ProjectBusy;
			ApplyBlocker(status, await GetQueueBlockerAsync(
				slotHolders.Where(j => j.ProjectId == job.ProjectId && (job.SwarmId == null || j.SwarmId != job.SwarmId)),
				cancellationToken));
			return status;
		}

		if (hasProvider)
		{
			var providerBlocker = await GetQueueBlockerAsync(slotHolders.Where(j => j.ProviderId == job.ProviderId), cancellationToken);
			if (providerBlocker != null)
			{
				status.Reason = JobQueueWaitReason.ProviderBusy;
				ApplyBlocker(status, providerBlocker);
				return status;
			}
		}

		status.Reason = JobQueueWaitReason.StartingSoon;
		return status;
	}

	private sealed record QueueBlocker(Guid Id, string? Title, string GoalPrompt, string? ProjectName, JobStatus Status);

	private static async Task<QueueBlocker?> GetQueueBlockerAsync(IQueryable<Job> candidates, CancellationToken cancellationToken)
	{
		return await candidates
			.AsNoTracking()
			.OrderBy(j => j.StartedAt ?? j.CreatedAt)
			.Select(j => new QueueBlocker(j.Id, j.Title, j.GoalPrompt, j.Project != null ? j.Project.Name : null, j.Status))
			.FirstOrDefaultAsync(cancellationToken);
	}

	private static void ApplyBlocker(JobQueueStatus status, QueueBlocker? blocker)
	{
		if (blocker == null)
		{
			return;
		}

		var title = string.IsNullOrWhiteSpace(blocker.Title) ? blocker.GoalPrompt : blocker.Title;
		title = title.ReplaceLineEndings(" ").Trim();
		status.BlockingJobId = blocker.Id;
		status.BlockingJobTitle = title.Length > QueueStatusTitleMaxLength
			? title[..(QueueStatusTitleMaxLength - 1)].TrimEnd() + "…"
			: title;
		status.BlockingProjectName = blocker.ProjectName;
		status.BlockingJobStatus = blocker.Status;
	}
}
