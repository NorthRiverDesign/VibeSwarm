using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Data;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Pausing lives in the database rather than in the worker's memory: an unattended queue
/// has to stay stopped across a restart, and the person who stopped it is usually not
/// watching when the service comes back.
/// </summary>
public class JobQueueControlService : IJobQueueControlService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IJobService _jobService;
	private readonly ILogger<JobQueueControlService> _logger;
	private readonly IJobUpdateService? _jobUpdateService;

	public JobQueueControlService(
		VibeSwarmDbContext dbContext,
		IJobService jobService,
		ILogger<JobQueueControlService> logger,
		IJobUpdateService? jobUpdateService = null)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_logger = logger;
		_jobUpdateService = jobUpdateService;
	}

	public async Task<JobQueueState> GetStateAsync(CancellationToken cancellationToken = default)
	{
		var settings = await _dbContext.AppSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
		return await BuildStateAsync(
			settings?.JobQueuePaused ?? false,
			settings?.JobQueuePausedReason,
			settings?.JobQueuePausedAt,
			cancelledJobs: 0,
			cancellationToken);
	}

	public async Task<JobQueueState> PauseAsync(
		string? reason = null,
		bool cancelRunningJobs = false,
		CancellationToken cancellationToken = default)
	{
		var settings = await GetOrCreateSettingsAsync(cancellationToken);
		var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

		settings.JobQueuePaused = true;
		settings.JobQueuePausedReason = trimmedReason;
		settings.JobQueuePausedAt = DateTime.UtcNow;
		await _dbContext.SaveChangesAsync(cancellationToken);

		var cancelled = 0;
		if (cancelRunningJobs)
		{
			cancelled = await CancelInFlightJobsAsync(cancellationToken);
		}

		_logger.LogWarning(
			"Job queue paused ({Reason}). {Cancelled} running job(s) asked to stop.",
			trimmedReason ?? "no reason given",
			cancelled);

		await NotifyStateChangedAsync(true);

		return await BuildStateAsync(true, trimmedReason, settings.JobQueuePausedAt, cancelled, cancellationToken);
	}

	public async Task<JobQueueState> ResumeAsync(CancellationToken cancellationToken = default)
	{
		var settings = await GetOrCreateSettingsAsync(cancellationToken);

		settings.JobQueuePaused = false;
		settings.JobQueuePausedReason = null;
		settings.JobQueuePausedAt = null;
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Job queue resumed");
		await NotifyStateChangedAsync(false);

		return await BuildStateAsync(false, null, null, cancelledJobs: 0, cancellationToken);
	}

	/// <summary>
	/// Asks every job that is not already finished to stop. Cancellation is cooperative,
	/// so this marks them and the worker unwinds them at its next checkpoint.
	/// </summary>
	private async Task<int> CancelInFlightJobsAsync(CancellationToken cancellationToken)
	{
		var inFlight = await _dbContext.Jobs
			.Where(job =>
				job.Status == JobStatus.New ||
				job.Status == JobStatus.Pending ||
				job.Status == JobStatus.Processing ||
				job.Status == JobStatus.Paused)
			.Select(job => job.Id)
			.ToListAsync(cancellationToken);

		var cancelled = 0;
		foreach (var jobId in inFlight)
		{
			try
			{
				if (await _jobService.RequestCancellationAsync(jobId, cancellationToken))
				{
					cancelled++;
				}
			}
			catch (Exception ex)
			{
				// One job refusing to stop must not leave the rest running.
				_logger.LogWarning(ex, "Failed to request cancellation for job {JobId} while stopping the queue", jobId);
			}
		}

		return cancelled;
	}

	private async Task<AppSettings> GetOrCreateSettingsAsync(CancellationToken cancellationToken)
	{
		var settings = await _dbContext.AppSettings.FirstOrDefaultAsync(cancellationToken);
		if (settings != null)
		{
			return settings;
		}

		settings = new AppSettings { Id = Guid.NewGuid() };
		_dbContext.AppSettings.Add(settings);
		return settings;
	}

	private async Task<JobQueueState> BuildStateAsync(
		bool isPaused,
		string? reason,
		DateTime? pausedAt,
		int cancelledJobs,
		CancellationToken cancellationToken)
	{
		return new JobQueueState
		{
			IsPaused = isPaused,
			PausedReason = reason,
			PausedAt = pausedAt,
			RunningJobs = await _dbContext.Jobs.CountAsync(job => job.Status == JobStatus.Processing, cancellationToken),
			PendingJobs = await _dbContext.Jobs.CountAsync(
				job => job.Status == JobStatus.Pending || job.Status == JobStatus.New,
				cancellationToken),
			CancelledJobs = cancelledJobs
		};
	}

	private async Task NotifyStateChangedAsync(bool isPaused)
	{
		if (_jobUpdateService == null)
		{
			return;
		}

		try
		{
			await _jobUpdateService.NotifyJobQueuePausedChanged(isPaused);
		}
		catch
		{
			// A missed notification only delays the UI catching up; it must not fail the stop.
		}
	}
}
