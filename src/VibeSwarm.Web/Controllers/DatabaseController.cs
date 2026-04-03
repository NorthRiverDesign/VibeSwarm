using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/database")]
[Authorize]
public class DatabaseController : ControllerBase
{
	private static readonly JsonSerializerOptions ExportJsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() },
	};

	private readonly IDatabaseService _databaseService;

	public DatabaseController(IDatabaseService databaseService) => _databaseService = databaseService;

	[HttpGet("export")]
	public async Task<IActionResult> Export(CancellationToken ct)
	{
		var export = await _databaseService.ExportAsync(ct);
		var json = JsonSerializer.Serialize(export, ExportJsonOptions);
		var bytes = Encoding.UTF8.GetBytes(json);
		var filename = $"vibeswarm-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
		return File(bytes, "application/json", filename);
	}

	[HttpGet("storage")]
	public async Task<IActionResult> GetStorageSummary(CancellationToken ct)
		=> Ok(await _databaseService.GetStorageSummaryAsync(ct));

	[HttpGet("configuration")]
	public async Task<IActionResult> GetConfiguration(CancellationToken ct)
		=> Ok(await _databaseService.GetConfigurationAsync(ct));

	[HttpPost("import")]
	public async Task<IActionResult> Import([FromBody] DatabaseExportDto export, CancellationToken ct)
	{
		try
		{
			var result = await _databaseService.ImportAsync(export, ct);
			return Ok(result);
		}
		catch (Exception ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}

	[HttpPost("migrate")]
	public async Task<IActionResult> Migrate([FromBody] DatabaseMigrationRequest request, CancellationToken ct)
	{
		try
		{
			var result = await _databaseService.MigrateAsync(request, ct);
			return Ok(result);
		}
		catch (Exception ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}

	[HttpPost("maintenance")]
	public async Task<IActionResult> RunMaintenance([FromBody] DatabaseMaintenanceRequest request, CancellationToken ct)
	{
		try
		{
			var result = await _databaseService.RunMaintenanceAsync(request, ct);
			return Ok(result);
		}
		catch (Exception ex)
		{
			return BadRequest(new { error = ex.Message });
		}
	}
}
