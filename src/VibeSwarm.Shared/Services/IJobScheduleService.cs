using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IJobScheduleService
{
	Task<IEnumerable<JobSchedule>> GetAllAsync(CancellationToken cancellationToken = default);
	Task<JobSchedule?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<JobSchedule> CreateAsync(JobSchedule schedule, CancellationToken cancellationToken = default);
	Task<JobSchedule> UpdateAsync(JobSchedule schedule, CancellationToken cancellationToken = default);
	Task<JobSchedule> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
