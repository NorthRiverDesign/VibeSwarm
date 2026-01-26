using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface ISkillService
{
	Task<IEnumerable<Skill>> GetAllAsync(CancellationToken cancellationToken = default);
	Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken cancellationToken = default);
	Task<Skill?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
	Task<Skill> CreateAsync(Skill skill, CancellationToken cancellationToken = default);
	Task<Skill> UpdateAsync(Skill skill, CancellationToken cancellationToken = default);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
	Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default);
}
