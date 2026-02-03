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
    private readonly IProviderUsageService _usageService;

    public ProvidersController(IProviderService providerService, IProviderUsageService usageService)
    {
        _providerService = providerService;
        _usageService = usageService;
    }

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

    // Usage tracking endpoints

    [HttpGet("{id:guid}/usage")]
    public async Task<IActionResult> GetUsage(Guid id, CancellationToken ct)
    {
        var summary = await _usageService.GetUsageSummaryAsync(id, ct);
        return summary == null ? NotFound() : Ok(summary);
    }

    [HttpGet("usage-summaries")]
    public async Task<IActionResult> GetAllUsageSummaries(CancellationToken ct)
        => Ok(await _usageService.GetAllUsageSummariesAsync(ct));

    [HttpPost("{id:guid}/update-cli")]
    public async Task<IActionResult> UpdateCli(Guid id, CancellationToken ct)
    {
        var result = await _providerService.UpdateCliAsync(id, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:guid}/check-exhaustion")]
    public async Task<IActionResult> CheckExhaustion(Guid id, [FromQuery] int threshold = 80, CancellationToken ct = default)
    {
        var warning = await _usageService.CheckExhaustionAsync(id, threshold, ct);
        return Ok(warning);
    }

    [HttpPost("{id:guid}/reset-usage")]
    public async Task<IActionResult> ResetUsage(Guid id, CancellationToken ct)
    {
        await _usageService.ResetPeriodAsync(id, ct);
        return Ok();
    }

    public record SetDefaultModelRequest(Guid ModelId);
}
