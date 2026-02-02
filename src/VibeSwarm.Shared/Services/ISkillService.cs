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

	/// <summary>
	/// Expands a skill description using AI to generate detailed content
	/// </summary>
	/// <param name="description">The brief description to expand</param>
	/// <param name="providerId">The provider to use for expansion</param>
	/// <param name="modelId">Optional specific model to use</param>
	/// <param name="cancellationToken">Cancellation token</param>
	/// <returns>The expanded skill content, or null if expansion failed</returns>
	Task<string?> ExpandSkillAsync(string description, Guid providerId, string? modelId = null, CancellationToken cancellationToken = default);
}
