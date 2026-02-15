using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// EF Core implementation for managing inference providers and model assignments.
/// </summary>
public class InferenceProviderService : IInferenceProviderService
{
	private readonly VibeSwarmDbContext _db;

	public InferenceProviderService(VibeSwarmDbContext db) => _db = db;

	public async Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default)
		=> await _db.InferenceProviders
			.Include(p => p.Models)
			.OrderBy(p => p.Name)
			.ToListAsync(ct);

	public async Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default)
		=> await _db.InferenceProviders
			.Include(p => p.Models)
			.FirstOrDefaultAsync(p => p.Id == id, ct);

	public async Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default)
		=> await _db.InferenceProviders
			.Include(p => p.Models)
			.Where(p => p.IsEnabled)
			.OrderBy(p => p.Name)
			.ToListAsync(ct);

	public async Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default)
	{
		provider.Id = Guid.NewGuid();
		provider.CreatedAt = DateTime.UtcNow;
		provider.UpdatedAt = null;

		_db.InferenceProviders.Add(provider);
		await _db.SaveChangesAsync(ct);

		return provider;
	}

	public async Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default)
	{
		var existing = await _db.InferenceProviders.FindAsync([provider.Id], ct)
			?? throw new KeyNotFoundException($"Inference provider {provider.Id} not found");

		existing.Name = provider.Name;
		existing.ProviderType = provider.ProviderType;
		existing.Endpoint = provider.Endpoint;
		existing.ApiKey = provider.ApiKey;
		existing.IsEnabled = provider.IsEnabled;
		existing.UpdatedAt = DateTime.UtcNow;

		await _db.SaveChangesAsync(ct);
		return existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken ct = default)
	{
		var provider = await _db.InferenceProviders.FindAsync([id], ct)
			?? throw new KeyNotFoundException($"Inference provider {id} not found");

		_db.InferenceProviders.Remove(provider);
		await _db.SaveChangesAsync(ct);
	}

	public async Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
		=> await _db.InferenceModels
			.Where(m => m.InferenceProviderId == providerId)
			.OrderBy(m => m.ModelId)
			.ToListAsync(ct);

	public async Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default)
	{
		// Clear existing default for this task type under this provider
		var existingDefaults = await _db.InferenceModels
			.Where(m => m.InferenceProviderId == providerId && m.TaskType == taskType && m.IsDefault)
			.ToListAsync(ct);

		foreach (var model in existingDefaults)
			model.IsDefault = false;

		// Find the target model and set its task type + default
		var target = await _db.InferenceModels
			.FirstOrDefaultAsync(m => m.InferenceProviderId == providerId && m.ModelId == modelId, ct);

		if (target != null)
		{
			target.TaskType = taskType;
			target.IsDefault = true;
			target.UpdatedAt = DateTime.UtcNow;
		}

		await _db.SaveChangesAsync(ct);
	}

	public async Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default)
	{
		// Try to find a default model for the specific task type from an enabled provider
		var model = await _db.InferenceModels
			.Include(m => m.InferenceProvider)
			.Where(m => m.InferenceProvider != null && m.InferenceProvider.IsEnabled
				&& m.TaskType == taskType && m.IsDefault && m.IsAvailable)
			.FirstOrDefaultAsync(ct);

		if (model != null)
			return model;

		// Fallback to the "default" task type
		if (taskType != "default")
		{
			model = await _db.InferenceModels
				.Include(m => m.InferenceProvider)
				.Where(m => m.InferenceProvider != null && m.InferenceProvider.IsEnabled
					&& m.TaskType == "default" && m.IsDefault && m.IsAvailable)
				.FirstOrDefaultAsync(ct);
		}

		return model;
	}
}
