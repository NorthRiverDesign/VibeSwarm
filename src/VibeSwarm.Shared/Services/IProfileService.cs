using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IProfileService
{
	Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default);
	Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default);

	/// <summary>
	/// Saves the profile and writes its provider, model and reasoning through to every project
	/// setting linked to it.
	/// </summary>
	Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default);

	/// <summary>Deletes the profile. Linked project settings keep the values they last had.</summary>
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
