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

		schedule.Id = Guid.NewGuid();
		schedule.CreatedAt = DateTime.UtcNow;
		schedule.UpdatedAt = null;
		schedule.NextRunAtUtc = JobScheduleCalculator.CalculateNextRunUtc(schedule, DateTime.UtcNow);
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
		existing.ProviderId = schedule.ProviderId;
		existing.Prompt = schedule.Prompt;
		existing.ModelId = schedule.ModelId;
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

		existing.NextRunAtUtc = JobScheduleCalculator.CalculateNextRunUtc(existing, DateTime.UtcNow);
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
		schedule.NextRunAtUtc = JobScheduleCalculator.CalculateNextRunUtc(schedule, DateTime.UtcNow);
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
			.Include(schedule => schedule.Provider);
	}

	private async Task ValidateReferencesAsync(JobSchedule schedule, CancellationToken cancellationToken)
	{
		var projectExists = await _dbContext.Projects.AnyAsync(project => project.Id == schedule.ProjectId, cancellationToken);
		if (!projectExists)
		{
			throw new InvalidOperationException("The selected project was not found.");
		}

		var providerExists = await _dbContext.Providers
			.AnyAsync(provider => provider.Id == schedule.ProviderId && provider.IsEnabled, cancellationToken);
		if (!providerExists)
		{
			throw new InvalidOperationException("The selected provider is not enabled.");
		}

		if (string.IsNullOrWhiteSpace(schedule.ModelId))
		{
			return;
		}

		var modelExists = await _dbContext.ProviderModels
			.AnyAsync(model =>
				model.ProviderId == schedule.ProviderId &&
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
	}
}
