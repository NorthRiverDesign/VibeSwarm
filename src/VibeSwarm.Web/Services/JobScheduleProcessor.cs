using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

public class JobScheduleProcessor
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IJobService _jobService;
	private readonly IIdeaService _ideaService;
	private readonly ILogger<JobScheduleProcessor> _logger;

	public JobScheduleProcessor(
		VibeSwarmDbContext dbContext,
		IJobService jobService,
		IIdeaService ideaService,
		ILogger<JobScheduleProcessor> logger)
	{
		_dbContext = dbContext;
		_jobService = jobService;
		_ideaService = ideaService;
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
			if (schedule.ScheduleType == JobScheduleType.GenerateIdeas)
			{
				var result = await _ideaService.SuggestIdeasFromCodebaseAsync(
					schedule.ProjectId,
					new SuggestIdeasRequest
					{
						UseInference = true,
						ProviderId = schedule.InferenceProviderId,
						ModelId = schedule.ModelId,
						IdeaCount = schedule.IdeaCount,
						AdditionalContext = BuildScheduledIdeaContext(schedule, scheduledForUtc)
					},
					cancellationToken);
				if (!result.Success)
				{
					throw new InvalidOperationException(result.Message);
				}
			}
			else
			{
				var resolvedExecution = await ResolveScheduledExecutionAsync(schedule, cancellationToken);
				await _jobService.CreateAsync(new Job
				{
					ProjectId = schedule.ProjectId,
					ProviderId = resolvedExecution.ProviderId,
					GoalPrompt = schedule.Prompt,
					ModelUsed = resolvedExecution.ModelId,
					ReasoningEffort = resolvedExecution.ReasoningEffort,
					IsScheduled = true,
					JobScheduleId = schedule.Id,
					ScheduledForUtc = scheduledForUtc,
					TeamRoleId = resolvedExecution.TeamRoleId
				}, cancellationToken);
			}

			schedule.LastRunAtUtc = scheduledForUtc;
			schedule.LastError = null;
			await _dbContext.SaveChangesAsync(cancellationToken);
			_logger.LogInformation(
				"Processed scheduled {ScheduleType} for schedule {ScheduleId} at {ScheduledForUtc}",
				schedule.ScheduleType,
				schedule.Id,
				scheduledForUtc);
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

	private async Task<ScheduledExecutionSelection> ResolveScheduledExecutionAsync(JobSchedule schedule, CancellationToken cancellationToken)
	{
		if (schedule.ExecutionTarget == JobScheduleExecutionTarget.Provider)
		{
			if (!schedule.ProviderId.HasValue || schedule.ProviderId == Guid.Empty)
			{
				throw new InvalidOperationException("The selected provider is not enabled.");
			}

			return new ScheduledExecutionSelection(
				schedule.ProviderId.Value,
				schedule.ModelId,
				null,
				null);
		}

		if (!schedule.TeamRoleId.HasValue || schedule.TeamRoleId == Guid.Empty)
		{
			throw new InvalidOperationException("The selected agent is not assigned to this project.");
		}

		var assignment = await _dbContext.ProjectTeamRoles
			.Include(projectTeamRole => projectTeamRole.TeamRole)
			.Include(projectTeamRole => projectTeamRole.Provider)
			.FirstOrDefaultAsync(projectTeamRole =>
				projectTeamRole.ProjectId == schedule.ProjectId &&
				projectTeamRole.TeamRoleId == schedule.TeamRoleId.Value,
				cancellationToken);
		if (assignment == null || !assignment.IsEnabled || assignment.TeamRole == null || !assignment.TeamRole.IsEnabled)
		{
			throw new InvalidOperationException("The selected agent is not assigned to this project.");
		}

		if (assignment.Provider == null || !assignment.Provider.IsEnabled)
		{
			throw new InvalidOperationException("The selected agent does not have an enabled provider assignment.");
		}

		if (!string.IsNullOrWhiteSpace(schedule.ModelId))
		{
			var modelExists = await _dbContext.ProviderModels
				.AnyAsync(model =>
					model.ProviderId == assignment.ProviderId &&
					model.IsAvailable &&
					model.ModelId == schedule.ModelId,
					cancellationToken);
			if (!modelExists)
			{
				throw new InvalidOperationException("The selected model is not available for the chosen provider.");
			}
		}

		return new ScheduledExecutionSelection(
			assignment.ProviderId,
			string.IsNullOrWhiteSpace(schedule.ModelId) ? assignment.PreferredModelId : schedule.ModelId,
			assignment.PreferredReasoningEffort,
			assignment.TeamRoleId);
	}

	private static string BuildScheduledIdeaContext(JobSchedule schedule, DateTime scheduledForUtc)
	{
		var scheduleDescriptor = schedule.Frequency switch
		{
			JobScheduleFrequency.Hourly => $"hourly at minute {schedule.MinuteUtc:D2}",
			JobScheduleFrequency.Weekly => $"weekly on {schedule.WeeklyDay} at {schedule.HourUtc:D2}:{schedule.MinuteUtc:D2} UTC",
			JobScheduleFrequency.Monthly => $"monthly on day {schedule.DayOfMonth} at {schedule.HourUtc:D2}:{schedule.MinuteUtc:D2} UTC",
			_ => $"daily at {schedule.HourUtc:D2}:{schedule.MinuteUtc:D2} UTC"
		};

		return $"This idea-generation request comes from the scheduler. Schedule cadence: {scheduleDescriptor}. Scheduled run time: {scheduledForUtc:O}. Generate fresh ideas that are meaningfully different from what a recent run of this same recurring schedule would have already proposed.";
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

	private sealed record ScheduledExecutionSelection(Guid ProviderId, string? ModelId, string? ReasoningEffort, Guid? TeamRoleId);
}
