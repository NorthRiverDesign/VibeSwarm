using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface ITeamRoleService
{
	Task<IEnumerable<TeamRole>> GetAllAsync(CancellationToken cancellationToken = default);
	Task<IEnumerable<TeamRole>> GetEnabledAsync(CancellationToken cancellationToken = default);
	Task<TeamRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<TeamRole> CreateAsync(TeamRole teamRole, CancellationToken cancellationToken = default);
	Task<TeamRole> UpdateAsync(TeamRole teamRole, CancellationToken cancellationToken = default);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
	Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default);
}
