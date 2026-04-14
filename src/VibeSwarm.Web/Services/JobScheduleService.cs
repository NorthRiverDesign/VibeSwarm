using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Services;

public class JobScheduleService : IJobScheduleService
{
	private readonly VibeSwarmDbContext _dbContext;

	public JobScheduleService(VibeSwarmDbContext dbContext)
	{
		_dbContext = dbContext;
	}

	public async Task<IEnumerable<JobSchedule>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await BuildQuery()
			.OrderBy(schedule => schedule.IsEnabled ? 0 : 1)
			.ThenBy(schedule => schedule.NextRunAtUtc)
			.ThenBy(schedule => schedule.CreatedAt)
			.ToListAsync(cancellationToken);
	}

	public async Task<JobSchedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await BuildQuery()
			.FirstOrDefaultAsync(schedule => schedule.Id == id, cancellationToken);
	}

	public async Task<JobSchedule> CreateAsync(JobSchedule schedule, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(schedule);

		NormalizeSchedule(schedule);
		ValidationHelper.ValidateObject(schedule);
		await ValidateReferencesAsync(schedule, cancellationToken);
		var schedulerTimeZone = await GetSchedulerTimeZoneAsync(cancellationToken);

		schedule.Id = Guid.NewGuid();
		schedule.CreatedAt = DateTime.UtcNow;
		schedule.UpdatedAt = null;
		schedule.NextRunAtUtc = JobScheduleCalculator.CalculateNextRunUtc(schedule, DateTime.UtcNow, schedulerTimeZone);
		schedule.LastError = null;

		_dbContext.JobSchedules.Add(schedule);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(schedule.Id, cancellationToken) ?? schedule;
	}

	public async Task<JobSchedule> UpdateAsync(JobSchedule schedule, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(schedule);

		var existing = await _dbContext.JobSchedules
			.FirstOrDefaultAsync(item => item.Id == schedule.Id, cancellationToken);
		if (existing == null)
		{
			throw new InvalidOperationException($"Schedule with ID {schedule.Id} not found.");
		}

		existing.ProjectId = schedule.ProjectId;
		existing.ScheduleType = schedule.ScheduleType;
		existing.ExecutionTarget = schedule.ExecutionTarget;
		existing.ProviderId = schedule.ProviderId;
		existing.TeamRoleId = schedule.TeamRoleId;
		existing.InferenceProviderId = schedule.InferenceProviderId;
		existing.Prompt = schedule.Prompt;
		existing.ModelId = schedule.ModelId;
		existing.IdeaCount = schedule.IdeaCount;
		existing.Frequency = schedule.Frequency;
		existing.HourUtc = schedule.HourUtc;
		existing.MinuteUtc = schedule.MinuteUtc;
		existing.WeeklyDay = schedule.WeeklyDay;
		existing.DayOfMonth = schedule.DayOfMonth;
		existing.IsEnabled = schedule.IsEnabled;
		existing.UpdatedAt = DateTime.UtcNow;

		NormalizeSchedule(existing);
		ValidationHelper.ValidateObject(existing);
		await ValidateReferencesAsync(existing, cancellationToken);
		var schedulerTimeZone = await GetSchedulerTimeZoneAsync(cancellationToken);

		existing.NextRunAtUtc = JobScheduleCalculator.CalculateNextRunUtc(existing, DateTime.UtcNow, schedulerTimeZone);
		if (existing.IsEnabled)
		{
			existing.LastError = null;
		}

		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(existing.Id, cancellationToken) ?? existing;
	}

	public async Task<JobSchedule> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default)
	{
		var schedule = await _dbContext.JobSchedules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
		if (schedule == null)
		{
			throw new InvalidOperationException($"Schedule with ID {id} not found.");
		}

		schedule.IsEnabled = isEnabled;
		schedule.UpdatedAt = DateTime.UtcNow;
		var schedulerTimeZone = await GetSchedulerTimeZoneAsync(cancellationToken);
		schedule.NextRunAtUtc = JobScheduleCalculator.CalculateNextRunUtc(schedule, DateTime.UtcNow, schedulerTimeZone);
		if (isEnabled)
		{
			schedule.LastError = null;
		}

		await _dbContext.SaveChangesAsync(cancellationToken);

		return await GetByIdAsync(schedule.Id, cancellationToken) ?? schedule;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var schedule = await _dbContext.JobSchedules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
		if (schedule == null)
		{
			return;
		}

		_dbContext.JobSchedules.Remove(schedule);
		await _dbContext.SaveChangesAsync(cancellationToken);
	}

	private IQueryable<JobSchedule> BuildQuery()
	{
		return _dbContext.JobSchedules
			.Include(schedule => schedule.Project)
			.Include(schedule => schedule.Provider)
			.Include(schedule => schedule.TeamRole)
			.Include(schedule => schedule.InferenceProvider);
	}

	private async Task ValidateReferencesAsync(JobSchedule schedule, CancellationToken cancellationToken)
	{
		var projectExists = await _dbContext.Projects.AnyAsync(project => project.Id == schedule.ProjectId, cancellationToken);
		if (!projectExists)
		{
			throw new InvalidOperationException("The selected project was not found.");
		}

		if (schedule.ScheduleType == JobScheduleType.GenerateIdeas)
		{
			if (!schedule.InferenceProviderId.HasValue || schedule.InferenceProviderId == Guid.Empty)
			{
				throw new InvalidOperationException("The selected inference provider is not enabled.");
			}

			var inferenceProviderExists = await _dbContext.InferenceProviders
				.AnyAsync(provider => provider.Id == schedule.InferenceProviderId.Value && provider.IsEnabled, cancellationToken);
			if (!inferenceProviderExists)
			{
				throw new InvalidOperationException("The selected inference provider is not enabled.");
			}

			if (string.IsNullOrWhiteSpace(schedule.ModelId))
			{
				return;
			}

			var inferenceModelExists = await _dbContext.InferenceModels
				.AnyAsync(model =>
					model.InferenceProviderId == schedule.InferenceProviderId.Value &&
					model.IsAvailable &&
					model.ModelId == schedule.ModelId,
					cancellationToken);
			if (!inferenceModelExists)
			{
				throw new InvalidOperationException("The selected model is not available for the chosen inference provider.");
			}

			return;
		}

		if (schedule.ExecutionTarget == JobScheduleExecutionTarget.Provider)
		{
			if (!schedule.ProviderId.HasValue || schedule.ProviderId == Guid.Empty)
			{
				throw new InvalidOperationException("The selected provider is not enabled.");
			}

			var providerExists = await _dbContext.Providers
				.AnyAsync(provider => provider.Id == schedule.ProviderId.Value && provider.IsEnabled, cancellationToken);
			if (!providerExists)
			{
				throw new InvalidOperationException("The selected provider is not enabled.");
			}
		}
		else
		{
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

			schedule.ProviderId = assignment.ProviderId;
		}

		if (string.IsNullOrWhiteSpace(schedule.ModelId))
		{
			return;
		}

		var providerId = schedule.ProviderId;
		if (!providerId.HasValue || providerId == Guid.Empty)
		{
			throw new InvalidOperationException("The selected provider is not enabled.");
		}

		var modelExists = await _dbContext.ProviderModels
			.AnyAsync(model =>
				model.ProviderId == providerId.Value &&
				model.IsAvailable &&
				model.ModelId == schedule.ModelId,
				cancellationToken);
		if (!modelExists)
		{
			throw new InvalidOperationException("The selected model is not available for the chosen provider.");
		}
	}

	private static void NormalizeSchedule(JobSchedule schedule)
	{
		schedule.Prompt = schedule.Prompt?.Trim() ?? string.Empty;
		schedule.ModelId = string.IsNullOrWhiteSpace(schedule.ModelId) ? null : schedule.ModelId.Trim();
		schedule.LastError = string.IsNullOrWhiteSpace(schedule.LastError) ? null : schedule.LastError.Trim();
		schedule.ProviderId = schedule.ProviderId == Guid.Empty ? null : schedule.ProviderId;
		schedule.TeamRoleId = schedule.TeamRoleId == Guid.Empty ? null : schedule.TeamRoleId;
		schedule.InferenceProviderId = schedule.InferenceProviderId == Guid.Empty ? null : schedule.InferenceProviderId;
		schedule.IdeaCount = Math.Clamp(schedule.IdeaCount, ValidationLimits.JobScheduleIdeaCountMin, ValidationLimits.JobScheduleIdeaCountMax);
		if (schedule.ScheduleType == JobScheduleType.GenerateIdeas)
		{
			schedule.ProviderId = null;
			schedule.TeamRoleId = null;
			schedule.Prompt = string.Empty;
			schedule.ExecutionTarget = JobScheduleExecutionTarget.Provider;
			return;
		}

		schedule.InferenceProviderId = null;
		if (schedule.ExecutionTarget == JobScheduleExecutionTarget.Provider)
		{
			schedule.TeamRoleId = null;
		}
		else
		{
			schedule.ProviderId = null;
		}
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
