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
					AgentId = resolvedExecution.AgentId
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

		if (!schedule.AgentId.HasValue || schedule.AgentId == Guid.Empty)
		{
			throw new InvalidOperationException("An agent is required.");
		}

		var agent = await _dbContext.Agents
			.Include(a => a.DefaultProvider)
			.FirstOrDefaultAsync(a => a.Id == schedule.AgentId.Value, cancellationToken);
		if (agent == null || !agent.IsEnabled)
		{
			throw new InvalidOperationException("The selected agent does not exist or is disabled.");
		}

		if (!agent.DefaultProviderId.HasValue || agent.DefaultProvider == null || !agent.DefaultProvider.IsEnabled)
		{
			throw new InvalidOperationException("The selected agent does not have an enabled default provider.");
		}

		if (!string.IsNullOrWhiteSpace(schedule.ModelId))
		{
			var modelExists = await _dbContext.ProviderModels
				.AnyAsync(model =>
					model.ProviderId == agent.DefaultProviderId.Value &&
					model.IsAvailable &&
					model.ModelId == schedule.ModelId,
					cancellationToken);
			if (!modelExists)
			{
				throw new InvalidOperationException("The selected model is not available for the chosen provider.");
			}
		}

		return new ScheduledExecutionSelection(
			agent.DefaultProviderId.Value,
			string.IsNullOrWhiteSpace(schedule.ModelId) ? agent.DefaultModelId : schedule.ModelId,
			agent.DefaultReasoningEffort,
			agent.Id);
	}

	private static string BuildScheduledIdeaContext(JobSchedule schedule, DateTime scheduledForUtc)
	{
		var scheduleDescriptor = schedule.Frequency switch
		{
			JobScheduleFrequency.Minutes => $"every {schedule.IntervalMinutes} minutes",
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

	private sealed record ScheduledExecutionSelection(Guid ProviderId, string? ModelId, string? ReasoningEffort, Guid? AgentId);
}
