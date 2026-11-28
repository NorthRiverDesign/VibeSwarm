using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public interface IProfileService
{
	Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default);
	Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
	Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default);

	/// <summary>
	/// Saves the profile and writes its provider, model and reasoning through to every setting
	/// linked to it. A move to a provider one of those settings can't run on is refused.
	/// </summary>
	Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default);

	/// <summary>Deletes the profile. Every setting that used it moves to its provider's default profile.</summary>
	Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
