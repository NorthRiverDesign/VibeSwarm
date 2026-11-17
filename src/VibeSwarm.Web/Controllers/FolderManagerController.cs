using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/projects/folders")]
[Authorize]
public class FolderManagerController : ControllerBase
{
	private readonly IFolderManagerService _managerService;

	public FolderManagerController(IFolderManagerService managerService) => _managerService = managerService;

	[HttpGet("{folderId:guid}/manager/reports")]
	public async Task<IActionResult> GetReports(Guid folderId, CancellationToken ct)
		=> Ok(await _managerService.GetReportsAsync(folderId, ct));

	[HttpPost("{folderId:guid}/manager/runs")]
	public async Task<IActionResult> Run(Guid folderId, [FromBody] FolderManagerRunRequest request)
	{
		// A run keeps going when the page closes, so the report it pays for is still saved.
		using var timeout = new CancellationTokenSource(InferenceTimeouts.InferenceIdeaActionTimeout);
		return Ok(await _managerService.RunAsync(folderId, request ?? new FolderManagerRunRequest(), timeout.Token));
	}

	[HttpDelete("manager/reports/{reportId:guid}")]
	public async Task<IActionResult> DeleteReport(Guid reportId, CancellationToken ct)
	{
		await _managerService.DeleteReportAsync(reportId, ct);
		return Ok();
	}
}
