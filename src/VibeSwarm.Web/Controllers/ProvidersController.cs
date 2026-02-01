using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/providers")]
[Authorize]
public class ProvidersController : ControllerBase
{
    private readonly IProviderService _providerService;

    public ProvidersController(IProviderService providerService) => _providerService = providerService;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await _providerService.GetAllAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var provider = await _providerService.GetByIdAsync(id, ct);
        return provider == null ? NotFound() : Ok(provider);
    }

    [HttpGet("default")]
    public async Task<IActionResult> GetDefault(CancellationToken ct)
    {
        var provider = await _providerService.GetDefaultAsync(ct);
        return provider == null ? NoContent() : Ok(provider);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Provider provider, CancellationToken ct) => Ok(await _providerService.CreateAsync(provider, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Provider provider, CancellationToken ct)
    {
        provider.Id = id;
        return Ok(await _providerService.UpdateAsync(provider, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _providerService.DeleteAsync(id, ct); return Ok(); }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> TestConnection(Guid id, CancellationToken ct)
        => await _providerService.TestConnectionAsync(id, ct) ? Ok() : BadRequest();

    [HttpGet("{id:guid}/test-details")]
    public async Task<IActionResult> TestConnectionDetails(Guid id, CancellationToken ct)
        => Ok(await _providerService.TestConnectionWithDetailsAsync(id, ct));

    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct) { await _providerService.SetEnabledAsync(id, true, ct); return Ok(); }

    [HttpPost("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) { await _providerService.SetEnabledAsync(id, false, ct); return Ok(); }

    [HttpPost("{id:guid}/set-default")]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct) { await _providerService.SetDefaultAsync(id, ct); return Ok(); }

    [HttpGet("{providerId:guid}/models")]
    public async Task<IActionResult> GetModels(Guid providerId, CancellationToken ct) => Ok(await _providerService.GetModelsAsync(providerId, ct));

    [HttpPost("{providerId:guid}/refresh-models")]
    public async Task<IActionResult> RefreshModels(Guid providerId, CancellationToken ct) => Ok(await _providerService.RefreshModelsAsync(providerId, ct));

    [HttpPut("{providerId:guid}/default-model")]
    public async Task<IActionResult> SetDefaultModel(Guid providerId, [FromBody] SetDefaultModelRequest req, CancellationToken ct)
    {
        await _providerService.SetDefaultModelAsync(providerId, req.ModelId, ct);
        return Ok();
    }

    public record SetDefaultModelRequest(Guid ModelId);
}
