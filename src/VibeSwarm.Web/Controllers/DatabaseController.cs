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
}
