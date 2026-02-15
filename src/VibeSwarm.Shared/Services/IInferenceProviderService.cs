using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// CRUD service for managing inference providers and their model assignments.
/// </summary>
public interface IInferenceProviderService
{
	Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default);
	Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default);
	Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default);
	Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default);
	Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default);
	Task DeleteAsync(Guid id, CancellationToken ct = default);

	/// <summary>
	/// Gets the stored models for a specific inference provider.
	/// </summary>
	Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default);

	/// <summary>
	/// Assigns a model to a specific task type.
	/// </summary>
	Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default);

	/// <summary>
	/// Resolves which model to use for a given task type.
	/// Falls back to the "default" task type if no specific model is assigned.
	/// </summary>
	Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default);
}
