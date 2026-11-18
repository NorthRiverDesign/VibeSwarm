using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;

namespace VibeSwarm.Shared.Services;

public interface IJobTemplateService
{
	Task<IEnumerable<JobTemplate>> GetAllAsync(CancellationToken cancellationToken = default);
	Task<JobTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<JobTemplate> CreateAsync(JobTemplate template, CancellationToken cancellationToken = default);
	Task<JobTemplate> UpdateAsync(JobTemplate template, CancellationToken cancellationToken = default);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
	Task<JobTemplate> IncrementUseCountAsync(Guid id, CancellationToken cancellationToken = default);

	/// <summary>The latest job this template created on each project, newest first.</summary>
	Task<IReadOnlyList<JobTemplateProjectRun>> GetRunsAsync(Guid templateId, CancellationToken cancellationToken = default);

	/// <summary>Queues one job from the template on each project, skipping (with a reason) any it can't.</summary>
	Task<JobTemplateRunResult> RunAsync(Guid templateId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default);
}
