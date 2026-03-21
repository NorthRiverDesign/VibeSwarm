using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

public class JobScheduleProcessor
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IJobService _jobService;
	private readonly ILogger<JobScheduleProcessor> _logger;

	public JobScheduleProcessor(
		VibeSwarmDbContext dbContext,
		IJobService jobService,
		ILogger<JobScheduleProcessor> logger)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_logger = logger;
	}

	public async Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default)
	{
		var now = DateTime.UtcNow;
		var dueSchedules = await _dbContext.JobSchedules
			.Where(schedule => schedule.IsEnabled && schedule.NextRunAtUtc <= now)
			.OrderBy(schedule => schedule.NextRunAtUtc)
			.ToListAsync(cancellationToken);

		var createdCount = 0;
		foreach (var schedule in dueSchedules)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				break;
			}

			if (await TryQueueScheduledJobAsync(schedule, now, cancellationToken))
			{
				createdCount++;
			}
		}

		return createdCount;
	}

	private async Task<bool> TryQueueScheduledJobAsync(JobSchedule schedule, DateTime now, CancellationToken cancellationToken)
	{
		var scheduledForUtc = schedule.NextRunAtUtc;
		var schedulerTimeZone = await GetSchedulerTimeZoneAsync(cancellationToken);
		schedule.NextRunAtUtc = JobScheduleCalculator.CalculateNextRunUtc(schedule, scheduledForUtc, schedulerTimeZone);
		schedule.UpdatedAt = now;

		var alreadyQueued = await _dbContext.Jobs
			.AnyAsync(job => job.JobScheduleId == schedule.Id && job.ScheduledForUtc == scheduledForUtc, cancellationToken);
		if (alreadyQueued)
		{
			schedule.LastRunAtUtc = scheduledForUtc;
			schedule.LastError = null;
			await _dbContext.SaveChangesAsync(cancellationToken);
			return false;
		}

		try
		{
			await _jobService.CreateAsync(new Job
			{
				ProjectId = schedule.ProjectId,
				ProviderId = schedule.ProviderId,
				GoalPrompt = schedule.Prompt,
				ModelUsed = schedule.ModelId,
				IsScheduled = true,
				JobScheduleId = schedule.Id,
				ScheduledForUtc = scheduledForUtc
			}, cancellationToken);

			schedule.LastRunAtUtc = scheduledForUtc;
			schedule.LastError = null;
			await _dbContext.SaveChangesAsync(cancellationToken);
			_logger.LogInformation("Queued scheduled job for schedule {ScheduleId} at {ScheduledForUtc}", schedule.Id, scheduledForUtc);
			return true;
		}
		catch (DbUpdateException ex) when (IsDuplicateScheduledJobError(ex))
		{
			schedule.LastRunAtUtc = scheduledForUtc;
			schedule.LastError = null;
			await _dbContext.SaveChangesAsync(cancellationToken);
			return false;
		}
		catch (Exception ex)
		{
			schedule.LastError = ex.Message;
			await _dbContext.SaveChangesAsync(cancellationToken);
			_logger.LogWarning(ex, "Failed to queue scheduled job for schedule {ScheduleId}", schedule.Id);
			return false;
		}
	}

	private static bool IsDuplicateScheduledJobError(DbUpdateException exception)
	{
		var message = exception.InnerException?.Message ?? exception.Message;
		return message.Contains("IX_Jobs_JobScheduleId_ScheduledForUtc", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("UNIQUE constraint failed: Jobs.JobScheduleId, Jobs.ScheduledForUtc", StringComparison.OrdinalIgnoreCase);
	}

	private async Task<TimeZoneInfo> GetSchedulerTimeZoneAsync(CancellationToken cancellationToken)
	{
		var timeZoneId = await _dbContext.AppSettings
			.OrderBy(settings => settings.Id)
			.Select(settings => settings.TimeZoneId)
			.FirstOrDefaultAsync(cancellationToken);

		return DateTimeHelper.ResolveTimeZone(timeZoneId);
	}
}
