using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
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

		return Ok(await _providerService.RefreshModelsAsync(id, ct));
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
