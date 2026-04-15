using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IAgentService
{
	Task<IEnumerable<Agent>> GetAllAsync(CancellationToken cancellationToken = default);
	Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken cancellationToken = default);
	Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<Agent> CreateAsync(Agent agent, CancellationToken cancellationToken = default);
	Task<Agent> UpdateAsync(Agent agent, CancellationToken cancellationToken = default);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
	Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default);
}
