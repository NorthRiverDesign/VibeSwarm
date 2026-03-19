using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Business logic for managing auto-pilot iteration loops.
/// Called by the controller (start/stop/pause/resume) and by the background service (tick processing).
/// </summary>
public class AutoPilotService : IAutoPilotService
{
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IIdeaService _ideaService;
	private readonly IProviderUsageService _usageService;
	private readonly IJobUpdateService _jobUpdateService;
	private readonly ILogger<AutoPilotService> _logger;

	/// <summary>
	/// Terminal statuses for iteration loops — the loop is done and won't iterate again.
	/// </summary>
	private static readonly IterationLoopStatus[] TerminalStatuses =
	[
		IterationLoopStatus.Stopped,
		IterationLoopStatus.Exhausted,
		IterationLoopStatus.Failed
	];

	/// <summary>
	/// Terminal job statuses — the job has finished executing.
	/// </summary>
	private static readonly JobStatus[] TerminalJobStatuses =
	[
		JobStatus.Completed,
		JobStatus.Failed,
		JobStatus.Cancelled,
		JobStatus.Stalled
	];

	public AutoPilotService(
		VibeSwarmDbContext dbContext,
		IIdeaService ideaService,
		IProviderUsageService usageService,
		IJobUpdateService jobUpdateService,
		ILogger<AutoPilotService> logger)
	{
		_dbContext = dbContext;
		_ideaService = ideaService;
		_usageService = usageService;
		_jobUpdateService = jobUpdateService;
		_logger = logger;
	}

	#region Public API (called by controller)

	public async Task<IterationLoop> StartAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default)
	{
		// Verify project exists
		var project = await _dbContext.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
		if (project == null)
			throw new InvalidOperationException($"Project {projectId} not found.");

		// Ensure no active loop already exists
		var existing = await GetActiveLoopAsync(projectId, cancellationToken);
		if (existing != null)
			throw new InvalidOperationException($"Project already has an active auto-pilot loop (status: {existing.Status}).");

		var loop = new IterationLoop
		{
			Id = Guid.NewGuid(),
			ProjectId = projectId,
			Status = IterationLoopStatus.Running,
			ProviderId = config.ProviderId,
			ModelId = config.ModelId,
			MaxIterations = config.MaxIterations,
			MaxTotalCostUsd = config.MaxTotalCostUsd,
			MaxConsecutiveFailures = config.MaxConsecutiveFailures,
			CooldownSeconds = Math.Max(10, config.CooldownSeconds),
			AutoCommit = config.AutoCommit,
			AutoPush = config.AutoPush,
			StartedAt = DateTime.UtcNow
		};

		_dbContext.IterationLoops.Add(loop);
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Auto-pilot started for project {ProjectId} (loop {LoopId}, max {MaxIterations} iterations)",
			projectId, loop.Id, loop.MaxIterations);

		await NotifyStateChanged(loop);
		return loop;
	}

	public async Task StopAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var loop = await GetActiveLoopAsync(projectId, cancellationToken)
			?? throw new InvalidOperationException("No active auto-pilot loop for this project.");

		if (loop.CurrentJobId.HasValue)
		{
			// Job is running — request graceful stop
			loop.Status = IterationLoopStatus.Stopping;
			loop.LastStopReason = "User requested stop";
			_logger.LogInformation("Auto-pilot stopping for project {ProjectId} (waiting for current job)", projectId);
		}
		else
		{
			// No job running — stop immediately
			loop.Status = IterationLoopStatus.Stopped;
			loop.StoppedAt = DateTime.UtcNow;
			loop.LastStopReason = "User requested stop";
			_logger.LogInformation("Auto-pilot stopped for project {ProjectId}", projectId);
		}

		await _dbContext.SaveChangesAsync(cancellationToken);
		await NotifyStateChanged(loop);
	}

	public async Task PauseAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var loop = await GetActiveLoopAsync(projectId, cancellationToken)
			?? throw new InvalidOperationException("No active auto-pilot loop for this project.");

		if (loop.Status != IterationLoopStatus.Running)
			throw new InvalidOperationException($"Can only pause a running loop (current: {loop.Status}).");

		loop.Status = IterationLoopStatus.Paused;
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Auto-pilot paused for project {ProjectId}", projectId);
		await NotifyStateChanged(loop);
	}

	public async Task ResumeAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var loop = await GetActiveLoopAsync(projectId, cancellationToken)
			?? throw new InvalidOperationException("No active auto-pilot loop for this project.");

		if (loop.Status != IterationLoopStatus.Paused)
			throw new InvalidOperationException($"Can only resume a paused loop (current: {loop.Status}).");

		loop.Status = IterationLoopStatus.Running;
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Auto-pilot resumed for project {ProjectId}", projectId);
		await NotifyStateChanged(loop);
	}

	public async Task<IterationLoop?> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		// Return the most recent non-terminal loop, or the most recent terminal one
		return await _dbContext.IterationLoops
			.Where(l => l.ProjectId == projectId)
			.OrderByDescending(l => l.CreatedAt)
			.FirstOrDefaultAsync(cancellationToken);
	}

	public async Task<List<IterationLoop>> GetHistoryAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		return await _dbContext.IterationLoops
			.Where(l => l.ProjectId == projectId)
			.OrderByDescending(l => l.CreatedAt)
			.ToListAsync(cancellationToken);
	}

	public async Task<IterationLoop> UpdateConfigAsync(Guid projectId, AutoPilotConfig config, CancellationToken cancellationToken = default)
	{
		var loop = await GetActiveLoopAsync(projectId, cancellationToken)
			?? throw new InvalidOperationException("No active auto-pilot loop for this project.");

		if (loop.Status != IterationLoopStatus.Paused)
			throw new InvalidOperationException($"Can only update config on a paused loop (current: {loop.Status}).");

		loop.ProviderId = config.ProviderId;
		loop.ModelId = config.ModelId;
		loop.MaxIterations = config.MaxIterations;
		loop.MaxTotalCostUsd = config.MaxTotalCostUsd;
		loop.MaxConsecutiveFailures = config.MaxConsecutiveFailures;
		loop.CooldownSeconds = Math.Max(10, config.CooldownSeconds);
		loop.AutoCommit = config.AutoCommit;
		loop.AutoPush = config.AutoPush;

		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Auto-pilot config updated for project {ProjectId}", projectId);
		await NotifyStateChanged(loop);
		return loop;
	}

	#endregion

	#region Background Processing (called by AutoPilotBackgroundService)

	/// <summary>
	/// Gets all loops that need processing (Running status).
	/// </summary>
	public async Task<List<IterationLoop>> GetActiveLoopsAsync(CancellationToken cancellationToken)
	{
		return await _dbContext.IterationLoops
			.Where(l => l.Status == IterationLoopStatus.Running || l.Status == IterationLoopStatus.Stopping)
			.ToListAsync(cancellationToken);
	}

	/// <summary>
	/// Processes a single tick for an iteration loop. Called by the background service.
	/// </summary>
	public async Task ProcessTickAsync(Guid loopId, CancellationToken cancellationToken)
	{
		var loop = await _dbContext.IterationLoops.FindAsync([loopId], cancellationToken);
		if (loop == null) return;

		try
		{
			// 1. COOLDOWN CHECK
			if (loop.NextIterationAt.HasValue && loop.NextIterationAt.Value > DateTime.UtcNow)
				return;

			// 2. STOPPING CHECK — if stop requested and no job or job is done
			if (loop.Status == IterationLoopStatus.Stopping)
			{
				if (!loop.CurrentJobId.HasValue || await IsJobTerminalAsync(loop.CurrentJobId.Value, cancellationToken))
				{
					if (loop.CurrentJobId.HasValue)
						await EvaluateJobResultAsync(loop, cancellationToken);

					await StopLoopAsync(loop, loop.LastStopReason ?? "User requested stop", IterationLoopStatus.Stopped);
				}
				return;
			}

			// 3. CURRENT JOB CHECK
			if (loop.CurrentJobId.HasValue)
			{
				if (await IsJobTerminalAsync(loop.CurrentJobId.Value, cancellationToken))
				{
					await EvaluateJobResultAsync(loop, cancellationToken);
					// Fall through to guardrails + next iteration
				}
				else
				{
					return; // Job still running, wait
				}
			}

			// 4. GUARDRAILS
			if (loop.MaxIterations > 0 && loop.CompletedIterations >= loop.MaxIterations)
			{
				await StopLoopAsync(loop, $"Max iterations reached ({loop.MaxIterations})", IterationLoopStatus.Stopped);
				return;
			}

			if (loop.MaxTotalCostUsd.HasValue && loop.TotalCostUsd >= loop.MaxTotalCostUsd.Value)
			{
				await StopLoopAsync(loop, $"Cost limit reached (${loop.TotalCostUsd:F2} / ${loop.MaxTotalCostUsd:F2})", IterationLoopStatus.Stopped);
				return;
			}

			if (loop.ConsecutiveFailures >= loop.MaxConsecutiveFailures)
			{
				await StopLoopAsync(loop, $"Too many consecutive failures ({loop.ConsecutiveFailures})", IterationLoopStatus.Failed);
				return;
			}

			// 5. USAGE CHECK
			if (loop.ProviderId.HasValue)
			{
				var usageWarning = await _usageService.CheckExhaustionAsync(loop.ProviderId.Value, cancellationToken: cancellationToken);
				if (usageWarning?.IsExhausted == true)
				{
					loop.LastUsageCheckResult = usageWarning.Message;
					await StopLoopAsync(loop, $"Provider usage limit reached: {usageWarning.Message}", IterationLoopStatus.Exhausted);
					return;
				}
			}

			// 6. GENERATE IDEA
			var idea = await GenerateIdeaAsync(loop, cancellationToken);
			if (idea == null)
			{
				_logger.LogWarning("Auto-pilot could not generate idea for project {ProjectId}, will retry next tick", loop.ProjectId);
				loop.ConsecutiveFailures++;
				loop.NextIterationAt = DateTime.UtcNow.AddSeconds(loop.CooldownSeconds);
				await _dbContext.SaveChangesAsync(cancellationToken);
				await NotifyStateChanged(loop);

				if (loop.ConsecutiveFailures >= loop.MaxConsecutiveFailures)
				{
					await StopLoopAsync(loop, "Failed to generate improvement ideas", IterationLoopStatus.Failed);
				}
				return;
			}

			// 7. CREATE JOB FROM IDEA
			var job = await _ideaService.ConvertToJobAsync(idea.Id, cancellationToken);
			if (job == null)
			{
				_logger.LogWarning("Auto-pilot could not convert idea {IdeaId} to job", idea.Id);
				loop.ConsecutiveFailures++;
				loop.NextIterationAt = DateTime.UtcNow.AddSeconds(loop.CooldownSeconds);
				await _dbContext.SaveChangesAsync(cancellationToken);
				await NotifyStateChanged(loop);
				return;
			}

			// Tag the job with the loop ID
			job.IterationLoopId = loop.Id;
			await _dbContext.SaveChangesAsync(cancellationToken);

			// 8. UPDATE LOOP STATE
			loop.CurrentJobId = job.Id;
			loop.CurrentIdeaId = idea.Id;
			await _dbContext.SaveChangesAsync(cancellationToken);

			_logger.LogInformation("Auto-pilot iteration {Iteration} started for project {ProjectId}: job {JobId}",
				loop.CompletedIterations + 1, loop.ProjectId, job.Id);

			await NotifyStateChanged(loop);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error processing auto-pilot tick for loop {LoopId}", loopId);
		}
	}

	#endregion

	#region Private Helpers

	private async Task<IterationLoop?> GetActiveLoopAsync(Guid projectId, CancellationToken cancellationToken)
	{
		return await _dbContext.IterationLoops
			.Where(l => l.ProjectId == projectId && !TerminalStatuses.Contains(l.Status))
			.FirstOrDefaultAsync(cancellationToken);
	}

	private async Task<bool> IsJobTerminalAsync(Guid jobId, CancellationToken cancellationToken)
	{
		var job = await _dbContext.Jobs
			.AsNoTracking()
			.Where(j => j.Id == jobId)
			.Select(j => j.Status)
			.FirstOrDefaultAsync(cancellationToken);

		return TerminalJobStatuses.Contains(job);
	}

	private async Task EvaluateJobResultAsync(IterationLoop loop, CancellationToken cancellationToken)
	{
		if (!loop.CurrentJobId.HasValue) return;

		var job = await _dbContext.Jobs
			.AsNoTracking()
			.FirstOrDefaultAsync(j => j.Id == loop.CurrentJobId.Value, cancellationToken);

		loop.CurrentJobId = null;
		loop.CurrentIdeaId = null;
		loop.CompletedIterations++;
		loop.LastIterationAt = DateTime.UtcNow;
		loop.TotalCostUsd += job?.TotalCostUsd ?? 0;

		if (job?.Status == JobStatus.Completed)
		{
			loop.ConsecutiveFailures = 0;
			_logger.LogInformation("Auto-pilot iteration {Iteration} succeeded for project {ProjectId}",
				loop.CompletedIterations, loop.ProjectId);
		}
		else
		{
			loop.ConsecutiveFailures++;
			_logger.LogWarning("Auto-pilot iteration {Iteration} failed for project {ProjectId} (status: {Status}, error: {Error})",
				loop.CompletedIterations, loop.ProjectId, job?.Status, job?.ErrorMessage);
		}

		loop.NextIterationAt = DateTime.UtcNow.AddSeconds(loop.CooldownSeconds);
		await _dbContext.SaveChangesAsync(cancellationToken);
		await NotifyStateChanged(loop);
	}

	private async Task<Idea?> GenerateIdeaAsync(IterationLoop loop, CancellationToken cancellationToken)
	{
		var request = new SuggestIdeasRequest
		{
			UseInference = !loop.ProviderId.HasValue, // Use inference if no provider specified
			ProviderId = loop.ProviderId,
			ModelId = loop.ModelId,
			IdeaCount = 1
		};

		var result = await _ideaService.SuggestIdeasFromCodebaseAsync(loop.ProjectId, request, cancellationToken);

		if (!result.Success || result.Ideas.Count == 0)
		{
			_logger.LogWarning("Auto-pilot idea generation failed for project {ProjectId}: {Stage} - {Message}",
				loop.ProjectId, result.Stage, result.Message);
			return null;
		}

		return result.Ideas[0];
	}

	private async Task StopLoopAsync(IterationLoop loop, string reason, IterationLoopStatus status)
	{
		loop.Status = status;
		loop.StoppedAt = DateTime.UtcNow;
		loop.LastStopReason = reason;
		await _dbContext.SaveChangesAsync();

		_logger.LogInformation("Auto-pilot stopped for project {ProjectId}: {Reason} (status: {Status})",
			loop.ProjectId, reason, status);

		await NotifyStateChanged(loop);
	}

	private async Task NotifyStateChanged(IterationLoop loop)
	{
		try
		{
			await _jobUpdateService.NotifyAutoPilotStateChanged(loop.ProjectId, loop);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to send auto-pilot state notification for project {ProjectId}", loop.ProjectId);
		}
	}

	#endregion
}
