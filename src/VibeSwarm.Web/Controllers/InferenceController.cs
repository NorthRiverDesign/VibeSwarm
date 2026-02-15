using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.LocalInference;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/inference")]
[Authorize]
public class InferenceController : ControllerBase
{
	private readonly IInferenceProviderService _providerService;
	private readonly IInferenceService _inferenceService;

	public InferenceController(IInferenceProviderService providerService, IInferenceService inferenceService)
	{
		_providerService = providerService;
		_inferenceService = inferenceService;
	}

	// ---- Provider CRUD ----

	[HttpGet("providers")]
	public async Task<IActionResult> GetProviders(CancellationToken ct)
		=> Ok(await _providerService.GetAllAsync(ct));

	[HttpGet("providers/{id:guid}")]
	public async Task<IActionResult> GetProvider(Guid id, CancellationToken ct)
	{
		var provider = await _providerService.GetByIdAsync(id, ct);
		return provider == null ? NotFound() : Ok(provider);
	}

	[HttpPost("providers")]
	public async Task<IActionResult> CreateProvider([FromBody] InferenceProvider provider, CancellationToken ct)
		=> Ok(await _providerService.CreateAsync(provider, ct));

	[HttpPut("providers/{id:guid}")]
	public async Task<IActionResult> UpdateProvider(Guid id, [FromBody] InferenceProvider provider, CancellationToken ct)
	{
		provider.Id = id;
		return Ok(await _providerService.UpdateAsync(provider, ct));
	}

	[HttpDelete("providers/{id:guid}")]
	public async Task<IActionResult> DeleteProvider(Guid id, CancellationToken ct)
	{
		await _providerService.DeleteAsync(id, ct);
		return Ok();
	}

	// ---- Models ----

	[HttpGet("providers/{id:guid}/models")]
	public async Task<IActionResult> GetModels(Guid id, CancellationToken ct)
		=> Ok(await _providerService.GetModelsAsync(id, ct));

	[HttpPost("providers/{id:guid}/models/refresh")]
	public async Task<IActionResult> RefreshModels(Guid id, CancellationToken ct)
	{
		var provider = await _providerService.GetByIdAsync(id, ct);
		if (provider == null) return NotFound();

		var discovered = await _inferenceService.GetAvailableModelsAsync(provider.Endpoint, ct);

		// Sync discovered models into the database
		var dbContext = HttpContext.RequestServices.GetRequiredService<VibeSwarmDbContext>();
		var existingModels = await dbContext.InferenceModels
			.Where(m => m.InferenceProviderId == id)
			.ToListAsync(ct);

		var discoveredNames = discovered.Select(d => d.Name).ToHashSet();
		var existingNames = existingModels.Select(m => m.ModelId).ToHashSet();

		// Mark models no longer available
		foreach (var model in existingModels.Where(m => !discoveredNames.Contains(m.ModelId)))
			model.IsAvailable = false;

		// Add or update discovered models
		foreach (var disc in discovered)
		{
			var existing = existingModels.FirstOrDefault(m => m.ModelId == disc.Name);
			if (existing != null)
			{
				existing.DisplayName = disc.DisplayName;
				existing.ParameterSize = disc.ParameterSize;
				existing.Family = disc.Family;
				existing.QuantizationLevel = disc.QuantizationLevel;
				existing.SizeBytes = disc.SizeBytes;
				existing.IsAvailable = true;
				existing.UpdatedAt = DateTime.UtcNow;
			}
			else
			{
				dbContext.InferenceModels.Add(new InferenceModel
				{
					InferenceProviderId = id,
					ModelId = disc.Name,
					DisplayName = disc.DisplayName,
					ParameterSize = disc.ParameterSize,
					Family = disc.Family,
					QuantizationLevel = disc.QuantizationLevel,
					SizeBytes = disc.SizeBytes,
					IsAvailable = true
				});
			}
		}

		await dbContext.SaveChangesAsync(ct);

		var updatedModels = await _providerService.GetModelsAsync(id, ct);
		return Ok(updatedModels);
	}

	[HttpPut("models/{id:guid}/task")]
	public async Task<IActionResult> SetModelTask(Guid id, [FromBody] SetModelTaskRequest request, CancellationToken ct)
	{
		var dbContext = HttpContext.RequestServices.GetRequiredService<VibeSwarmDbContext>();
		var model = await dbContext.InferenceModels.FindAsync([id], ct);
		if (model == null) return NotFound();

		await _providerService.SetModelForTaskAsync(model.InferenceProviderId, model.ModelId, request.TaskType, ct);
		return Ok();
	}

	// ---- Health & Generation ----

	[HttpGet("health")]
	public async Task<IActionResult> CheckHealth([FromQuery] string? endpoint, CancellationToken ct)
		=> Ok(await _inferenceService.CheckHealthAsync(endpoint, ct));

	[HttpPost("generate")]
	public async Task<IActionResult> Generate([FromBody] InferenceRequest request, CancellationToken ct)
		=> Ok(await _inferenceService.GenerateAsync(request, ct));

	// ---- Request DTOs ----

	public record SetModelTaskRequest(string TaskType);
}
