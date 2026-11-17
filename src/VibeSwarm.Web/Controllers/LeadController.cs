using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/lead")]
[Authorize]
public class LeadController : ControllerBase
{
	private readonly ILeadService _lead;

	public LeadController(ILeadService lead) => _lead = lead;

	[HttpGet("projects/{projectId:guid}")]
	public async Task<IActionResult> GetStatus(Guid projectId, CancellationToken ct)
		=> Ok(await _lead.GetStatusAsync(projectId, ct));

	[HttpGet("projects/{projectId:guid}/reviews")]
	public async Task<IActionResult> GetReviews(Guid projectId, CancellationToken ct)
		=> Ok(await _lead.GetReviewsAsync(projectId, ct));

	[HttpPost("projects/{projectId:guid}/reviews")]
	public async Task<IActionResult> StartReview(Guid projectId, CancellationToken ct)
		=> Ok(await _lead.StartReviewAsync(projectId, ct));

	[HttpPost("projects/{projectId:guid}/reviews/cancel")]
	public async Task<IActionResult> CancelReview(Guid projectId, CancellationToken ct)
	{
		await _lead.CancelReviewAsync(projectId, ct);
		return Ok();
	}

	[HttpPost("suggestions/{ideaId:guid}/approve")]
	public async Task<IActionResult> Approve(Guid ideaId, CancellationToken ct)
	{
		var idea = await _lead.ApproveSuggestionAsync(ideaId, ct);
		return idea == null ? NotFound() : Ok(idea);
	}

	[HttpPost("suggestions/{ideaId:guid}/dismiss")]
	public async Task<IActionResult> Dismiss(Guid ideaId, CancellationToken ct)
	{
		await _lead.DismissSuggestionAsync(ideaId, ct);
		return Ok();
	}
}
