using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IJobTemplateService
{
	Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default);
	Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default);
	Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
	Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default);
}
