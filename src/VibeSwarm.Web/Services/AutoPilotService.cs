using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Business logic for managing auto-pilot iteration loops.
/// Called by the controller (start/stop/pause/resume) and by the background service (tick processing).
/// </summary>
/// <remarks>
/// A loop is meant to run unattended for hours, so only a real fault stops it. A usage limit
/// makes it wait for the reset, a round without a new idea moves it to the next focus area, and
/// every few changes it runs a polish pass over them instead of starting something new.
/// </remarks>
public class AutoPilotService : IAutoPilotService
{
	/// <summary>How long to wait for a usage limit whose reset time is unknown.</summary>
	internal static readonly TimeSpan UnknownResetWait = TimeSpan.FromMinutes(30);

	/// <summary>How long to rest after a full round of focus areas brought nothing new.</summary>
	internal static readonly TimeSpan NoIdeasRetryDelay = TimeSpan.FromMinutes(30);

	/// <summary>Margin after a reset time, so the provider has actually reset when the loop resumes.</summary>
	private static readonly TimeSpan ResetMargin = TimeSpan.FromMinutes(1);

	private const int StatusMessageMaxLength = 500;

	/// <summary>How many earlier auto-pilot jobs the idea source is told about.</summary>
	private const int RecentWorkCount = 20;

	private readonly VibeSwarmDbContext _dbContext;
	private readonly IIdeaService _ideaService;
	private readonly IJobService _jobService;
	private readonly IProviderUsageService _usageService;
	private readonly IJobUpdateService _jobUpdateService;
	private readonly ILogger<AutoPilotService> _logger;

	/// <summary>
	/// Per-loop lock to prevent overlapping tick processing when ticks take longer than the poll interval.
	/// </summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _loopLocks = new();

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
		IJobService jobService,
		IProviderUsageService usageService,
		IJobUpdateService jobUpdateService,
		ILogger<AutoPilotService> logger)
	{
		_dbContext = dbContext;
		_ideaService = ideaService;
		_jobService = jobService;
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
			StartedAt = DateTime.UtcNow
		};
		ApplyConfig(loop, config);
		await ValidateProvidersAsync(loop, cancellationToken);

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
			loop.StatusMessage = null;
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
		var loop = await _dbContext.IterationLoops
			.Where(l => l.ProjectId == projectId)
			.OrderByDescending(l => l.CreatedAt)
			.FirstOrDefaultAsync(cancellationToken);

		if (loop?.CurrentJobId is Guid jobId)
		{
			var job = await _dbContext.Jobs
				.AsNoTracking()
				.Where(j => j.Id == jobId)
				.Select(j => new { j.Title, j.Status, j.NotBeforeUtc })
				.FirstOrDefaultAsync(cancellationToken);

			loop.CurrentJobTitle = job?.Title;
			loop.CurrentJobStatus = job?.Status;
			loop.CurrentJobNotBeforeUtc = job?.NotBeforeUtc is DateTime notBefore && notBefore > DateTime.UtcNow ? notBefore : null;
		}

		return loop;
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

		ApplyConfig(loop, config);
		await ValidateProvidersAsync(loop, cancellationToken);

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
		var loopLock = _loopLocks.GetOrAdd(loopId, _ => new SemaphoreSlim(1, 1));
		if (!await loopLock.WaitAsync(0, cancellationToken))
		{
			return; // Previous tick still processing — skip this one
		}

		try
		{
		var loop = await _dbContext.IterationLoops.FindAsync([loopId], cancellationToken);
		if (loop == null) return;

		try
		{
			// 1. UNTRACKED JOB — a job created for this loop that was never recorded on it
			// (e.g. the app stopped in between) is picked up again rather than run twice.
			if (!loop.CurrentJobId.HasValue)
				await AdoptUntrackedJobAsync(loop, cancellationToken);

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
					// EvaluateJobResultAsync sets NextIterationAt, so the next iteration
					// waits for the cooldown instead of starting in this same tick.
					await EvaluateJobResultAsync(loop, cancellationToken);
				}
				return;
			}

			// 4. COOLDOWN CHECK — also covers waiting for a usage limit to reset
			if (loop.NextIterationAt.HasValue && loop.NextIterationAt.Value > DateTime.UtcNow)
				return;

			// 5. GUARDRAILS
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

			// 6. CODING PROVIDER — wait out usage limits instead of stopping
			var candidates = await GetCodingProviderCandidatesAsync(loop, cancellationToken);
			if (candidates.Count == 0)
			{
				var reason = loop.ProviderId.HasValue
					? "The chosen coding provider is disabled or was removed"
					: "No coding provider is enabled for this project";
				await StopLoopAsync(loop, reason, IterationLoopStatus.Failed);
				return;
			}

			var codingProvider = await GetFirstAvailableProviderAsync(loop, candidates, cancellationToken);
			if (codingProvider == null)
				return;

			// 7. POLISH PASS
			if (IsPolishDue(loop))
			{
				await StartPolishPassAsync(loop, cancellationToken);
				return;
			}

			// 8. GENERATE IDEA
			var ideaResult = await GenerateIdeaAsync(loop, codingProvider.Value, cancellationToken);
			if (ideaResult.Idea == null)
			{
				if (ideaResult.IsFatal)
					await StopLoopAsync(loop, ideaResult.Message, IterationLoopStatus.Failed);
				else
					await RecordIdeaMissAsync(loop, ideaResult.Message, cancellationToken);
				return;
			}

			// 9. CREATE JOB FROM IDEA
			var job = await _ideaService.ConvertToJobAsync(ideaResult.Idea.Id, new IdeaProcessingOptions
			{
				ProviderId = loop.ProviderId,
				ModelId = loop.ModelId,
				IterationLoopId = loop.Id,
				CommitModeOverride = GetCommitMode(loop)
			}, cancellationToken);
			if (job == null)
			{
				_logger.LogWarning("Auto-pilot could not convert idea {IdeaId} to job", ideaResult.Idea.Id);
				await RemoveLeftoverIdeaAsync(ideaResult.Idea.Id, null, cancellationToken);
				loop.ConsecutiveFailures++;
				loop.NextIterationAt = DateTime.UtcNow.AddSeconds(loop.CooldownSeconds);
				await _dbContext.SaveChangesAsync(cancellationToken);
				await NotifyStateChanged(loop);
				return;
			}

			// 10. UPDATE LOOP STATE
			await BeginIterationAsync(loop, job.Id, ideaResult.Idea.Id, cancellationToken);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error processing auto-pilot tick for loop {LoopId}", loopId);
		}
		}
		finally
		{
			loopLock.Release();
		}
	}

	#endregion

	#region Private Helpers

	private static void ApplyConfig(IterationLoop loop, AutoPilotConfig config)
	{
		loop.InferenceProviderId = config.InferenceProviderId;
		loop.InferenceModelId = string.IsNullOrWhiteSpace(config.InferenceModelId) ? null : config.InferenceModelId.Trim();
		loop.ProviderId = config.ProviderId;
		loop.ModelId = string.IsNullOrWhiteSpace(config.ModelId) ? null : config.ModelId.Trim();
		loop.MaxIterations = Math.Clamp(config.MaxIterations, 0, AutoPilotConfig.MaxIterationsLimit);
		loop.MaxTotalCostUsd = config.MaxTotalCostUsd is decimal cost && cost > 0 ? cost : null;
		loop.MaxConsecutiveFailures = Math.Clamp(config.MaxConsecutiveFailures, 1, AutoPilotConfig.MaxConsecutiveFailuresLimit);
		loop.CooldownSeconds = Math.Clamp(config.CooldownSeconds, AutoPilotConfig.MinCooldownSeconds, AutoPilotConfig.MaxCooldownSeconds);
		loop.PolishEveryIterations = Math.Clamp(config.PolishEveryIterations, 0, AutoPilotConfig.MaxPolishEveryIterations);
		loop.AutoPush = config.AutoPush;
	}

	/// <summary>
	/// Refuses a start the loop could never act on, so the mistake shows on the form rather
	/// than as a failed loop minutes later.
	/// </summary>
	private async Task ValidateProvidersAsync(IterationLoop loop, CancellationToken cancellationToken)
	{
		if (loop.InferenceProviderId is Guid inferenceProviderId
			&& !await _dbContext.InferenceProviders.AnyAsync(p => p.Id == inferenceProviderId && p.IsEnabled, cancellationToken))
		{
			throw new InvalidOperationException("The inference provider chosen for ideas is disabled or was removed.");
		}

		if (loop.ProviderId is Guid providerId
			&& !await _dbContext.Providers.AnyAsync(p => p.Id == providerId && p.IsEnabled, cancellationToken))
		{
			throw new InvalidOperationException("The coding provider chosen for auto-pilot is disabled or was removed.");
		}

		if ((await GetCodingProviderCandidatesAsync(loop, cancellationToken)).Count == 0)
		{
			throw new InvalidOperationException("Enable a coding provider before starting auto-pilot.");
		}
	}

	/// <summary>Auto-pilot always commits, so each iteration builds on the last.</summary>
	private static AutoCommitMode GetCommitMode(IterationLoop loop) =>
		loop.AutoPush ? AutoCommitMode.CommitAndPush : AutoCommitMode.CommitOnly;

	private async Task<IterationLoop?> GetActiveLoopAsync(Guid projectId, CancellationToken cancellationToken)
	{
		return await _dbContext.IterationLoops
			.Where(l => l.ProjectId == projectId && !TerminalStatuses.Contains(l.Status))
			.FirstOrDefaultAsync(cancellationToken);
	}

	private async Task AdoptUntrackedJobAsync(IterationLoop loop, CancellationToken cancellationToken)
	{
		var untrackedJobId = await _dbContext.Jobs
			.AsNoTracking()
			.Where(j => j.IterationLoopId == loop.Id && !TerminalJobStatuses.Contains(j.Status))
			.OrderByDescending(j => j.CreatedAt)
			.Select(j => (Guid?)j.Id)
			.FirstOrDefaultAsync(cancellationToken);

		if (untrackedJobId.HasValue)
		{
			_logger.LogInformation("Auto-pilot loop {LoopId} picked up its untracked job {JobId}", loop.Id, untrackedJobId);
			loop.CurrentJobId = untrackedJobId;
			await _dbContext.SaveChangesAsync(cancellationToken);
		}
	}

	private async Task<bool> IsJobTerminalAsync(Guid jobId, CancellationToken cancellationToken)
	{
		var status = await _dbContext.Jobs
			.AsNoTracking()
			.Where(j => j.Id == jobId)
			.Select(j => (JobStatus?)j.Status)
			.FirstOrDefaultAsync(cancellationToken);

		// A job that was deleted will never finish, so the loop moves on.
		return status == null || TerminalJobStatuses.Contains(status.Value);
	}

	private async Task EvaluateJobResultAsync(IterationLoop loop, CancellationToken cancellationToken)
	{
		if (!loop.CurrentJobId.HasValue) return;

		var jobId = loop.CurrentJobId.Value;
		var job = await _dbContext.Jobs
			.AsNoTracking()
			.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
		var ideaId = loop.CurrentIdeaId;

		loop.CurrentJobId = null;
		loop.CurrentIdeaId = null;
		loop.CompletedIterations++;
		loop.LastIterationAt = DateTime.UtcNow;
		loop.TotalCostUsd += job?.TotalCostUsd ?? 0;

		if (job?.Status == JobStatus.Completed)
		{
			loop.ConsecutiveFailures = 0;
			if (!AutoPilotPrompts.IsPolishJob(job))
				loop.IterationsSinceLastPolish++;

			_logger.LogInformation("Auto-pilot iteration {Iteration} succeeded for project {ProjectId}",
				loop.CompletedIterations, loop.ProjectId);
		}
		else
		{
			// A run cut short by its provider's usage limit says nothing about the idea. It
			// doesn't count toward the failure limit; the next tick waits for the reset.
			var hitUsageLimit = job != null && await GetProviderWaitAsync(job.ProviderId, cancellationToken) != null;
			if (!hitUsageLimit)
				loop.ConsecutiveFailures++;

			// The failed idea went back to the project's backlog. Auto-pilot already knows
			// it tried it, and leaving it there would let the ideas queue run it again.
			if (ideaId.HasValue)
				await RemoveLeftoverIdeaAsync(ideaId.Value, jobId, cancellationToken);

			_logger.LogWarning("Auto-pilot iteration {Iteration} failed for project {ProjectId} (status: {Status}, usage limited: {UsageLimited}, error: {Error})",
				loop.CompletedIterations, loop.ProjectId, job?.Status, hitUsageLimit, job?.ErrorMessage);
		}

		loop.NextIterationAt = DateTime.UtcNow.AddSeconds(loop.CooldownSeconds);
		await _dbContext.SaveChangesAsync(cancellationToken);
		await NotifyStateChanged(loop);
	}

	/// <summary>
	/// The providers a loop's job may run on, in the order the job would try them: the chosen
	/// provider first, then the project's own selection (or every enabled provider when the
	/// project has none), the same order the job's execution plan uses.
	/// </summary>
	private async Task<List<(Guid Id, string Name)>> GetCodingProviderCandidatesAsync(IterationLoop loop, CancellationToken cancellationToken)
	{
		var enabledProviders = (await _dbContext.Providers
			.AsNoTracking()
			.Where(p => p.IsEnabled)
			.OrderByDescending(p => p.IsDefault)
			.ThenBy(p => p.Name)
			.Select(p => new { p.Id, p.Name })
			.ToListAsync(cancellationToken))
			.Select(p => (p.Id, p.Name))
			.ToList();

		if (loop.ProviderId is Guid chosenId && enabledProviders.All(p => p.Id != chosenId))
		{
			return [];
		}

		var projectSelection = await _dbContext.ProjectProviders
			.AsNoTracking()
			.Where(pp => pp.ProjectId == loop.ProjectId && pp.IsEnabled)
			.OrderBy(pp => pp.Priority)
			.Select(pp => pp.ProviderId)
			.ToListAsync(cancellationToken);

		var ordered = projectSelection.Count == 0
			? enabledProviders
			: projectSelection
				.Select(id => enabledProviders.FirstOrDefault(p => p.Id == id))
				.Where(p => p.Id != Guid.Empty)
				.ToList();

		if (loop.ProviderId is Guid preferredId)
		{
			var preferred = enabledProviders.First(p => p.Id == preferredId);
			ordered = [preferred, .. ordered.Where(p => p.Id != preferredId)];
		}

		return ordered;
	}

	/// <summary>
	/// The first provider that can take a job now. When every one is held by a usage limit,
	/// the loop is set to wait until the earliest reset and null is returned.
	/// </summary>
	private async Task<(Guid Id, string Name)?> GetFirstAvailableProviderAsync(
		IterationLoop loop,
		List<(Guid Id, string Name)> candidates,
		CancellationToken cancellationToken)
	{
		(DateTime Until, string Reason)? earliestWait = null;
		string? primaryReason = null;

		foreach (var candidate in candidates)
		{
			var wait = await GetProviderWaitAsync(candidate.Id, cancellationToken);
			if (wait == null)
			{
				if (loop.StatusMessage != null)
				{
					loop.StatusMessage = null;
					await _dbContext.SaveChangesAsync(cancellationToken);
				}
				return candidate;
			}

			primaryReason ??= wait.Value.Reason;
			if (earliestWait == null || wait.Value.Until < earliestWait.Value.Until)
				earliestWait = wait;
		}

		var message = candidates.Count == 1
			? $"Waiting for usage to reset: {primaryReason}"
			: $"Waiting for usage to reset: {primaryReason}, and the project's other providers are limited too";

		loop.NextIterationAt = earliestWait!.Value.Until;
		loop.StatusMessage = Truncate(message, StatusMessageMaxLength);
		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Auto-pilot for project {ProjectId} waiting until {Until:u}: {Reason}",
			loop.ProjectId, loop.NextIterationAt, message);

		await NotifyStateChanged(loop);
		return null;
	}

	/// <summary>
	/// When a provider can next run a job, if a usage limit holds it now: an exhausted limit,
	/// a rate-limit backoff, or a session too full to fit another job.
	/// </summary>
	private async Task<(DateTime Until, string Reason)?> GetProviderWaitAsync(Guid providerId, CancellationToken cancellationToken)
	{
		var now = DateTime.UtcNow;
		(DateTime Until, string Reason)? wait = null;
		void Consider(DateTime until, string reason)
		{
			if (wait == null || until > wait.Value.Until)
				wait = (until, reason);
		}

		var providerName = await _dbContext.Providers
			.AsNoTracking()
			.Where(p => p.Id == providerId)
			.Select(p => p.Name)
			.FirstOrDefaultAsync(cancellationToken) ?? "The provider";

		var warning = await _usageService.CheckExhaustionAsync(providerId, cancellationToken: cancellationToken);
		if (warning?.IsExhausted == true)
		{
			var until = warning.ResetTime is DateTime reset && reset > now ? reset.Add(ResetMargin) : now.Add(UnknownResetWait);
			Consider(until, $"{providerName} reached its usage limit");
		}

		var nextAvailable = await _dbContext.ProviderUsageSummaries
			.AsNoTracking()
			.Where(s => s.ProviderId == providerId)
			.Select(s => s.NextExecutionAvailableAt)
			.FirstOrDefaultAsync(cancellationToken);
		if (nextAvailable is DateTime backoff && backoff > now)
		{
			Consider(backoff, $"{providerName} is rate limited");
		}

		var sessionHold = await ProviderSessionLimitGuard.GetHoldAsync(_dbContext, providerId, now, cancellationToken);
		if (sessionHold != null)
		{
			Consider(sessionHold.Until.Add(ResetMargin), $"{providerName}'s session is {sessionHold.PercentUsed}% used");
		}

		return wait;
	}

	private static bool IsPolishDue(IterationLoop loop)
	{
		if (loop.PolishEveryIterations <= 0 || loop.IterationsSinceLastPolish == 0)
			return false;

		// Polish on schedule, or early when a full round of focus areas found nothing new.
		return loop.IterationsSinceLastPolish >= loop.PolishEveryIterations
			|| loop.ConsecutiveIdeaMisses >= AutoPilotPrompts.FocusAreas.Count;
	}

	/// <summary>
	/// Queues a job that reviews and tidies the changes since the last polish pass. It needs no
	/// idea: its goal is built from those changes.
	/// </summary>
	private async Task StartPolishPassAsync(IterationLoop loop, CancellationToken cancellationToken)
	{
		var changeCount = loop.IterationsSinceLastPolish;
		var recentChanges = await _dbContext.Jobs
			.AsNoTracking()
			.Where(j => j.IterationLoopId == loop.Id
				&& j.Status == JobStatus.Completed
				&& (j.Tags == null || !j.Tags.Contains(AutoPilotPrompts.PolishJobTag)))
			.OrderByDescending(j => j.CreatedAt)
			.Take(changeCount)
			.Select(j => new { j.Title, j.Branch })
			.ToListAsync(cancellationToken);

		loop.IterationsSinceLastPolish = 0;
		loop.ConsecutiveIdeaMisses = 0;

		if (recentChanges.Count == 0)
		{
			// The jobs were deleted; there is nothing left to polish.
			await _dbContext.SaveChangesAsync(cancellationToken);
			return;
		}

		Job job;
		try
		{
			job = await _jobService.CreateAsync(new Job
			{
				ProjectId = loop.ProjectId,
				ProviderId = loop.ProviderId ?? Guid.Empty,
				ModelUsed = loop.ModelId,
				Branch = recentChanges[0].Branch,
				Title = AutoPilotPrompts.BuildPolishTitle(recentChanges.Count),
				GoalPrompt = AutoPilotPrompts.BuildPolishGoal(recentChanges.Select(c => c.Title ?? string.Empty).ToList()),
				Tags = AutoPilotPrompts.PolishJobTag,
				IterationLoopId = loop.Id,
				CommitModeOverride = GetCommitMode(loop)
			}, cancellationToken);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Auto-pilot could not queue a polish pass for project {ProjectId}", loop.ProjectId);
			loop.ConsecutiveFailures++;
			loop.NextIterationAt = DateTime.UtcNow.AddSeconds(loop.CooldownSeconds);
			await _dbContext.SaveChangesAsync(cancellationToken);
			await NotifyStateChanged(loop);
			return;
		}

		await BeginIterationAsync(loop, job.Id, null, cancellationToken);
	}

	private async Task BeginIterationAsync(IterationLoop loop, Guid jobId, Guid? ideaId, CancellationToken cancellationToken)
	{
		loop.CurrentJobId = jobId;
		loop.CurrentIdeaId = ideaId;
		loop.StatusMessage = null;
		if (ideaId.HasValue)
			loop.ConsecutiveIdeaMisses = 0;

		await _dbContext.SaveChangesAsync(cancellationToken);

		_logger.LogInformation("Auto-pilot iteration {Iteration} started for project {ProjectId}: job {JobId}",
			loop.CompletedIterations + 1, loop.ProjectId, jobId);

		await NotifyStateChanged(loop);
	}

	private sealed record IdeaResult(Idea? Idea, string Message, bool IsFatal = false);

	/// <summary>
	/// Asks for one new idea, telling the source what auto-pilot already did and which area to
	/// look at this round. When the inference provider can't answer, the coding provider is
	/// asked instead so the run keeps moving.
	/// </summary>
	private async Task<IdeaResult> GenerateIdeaAsync(IterationLoop loop, (Guid Id, string Name) codingProvider, CancellationToken cancellationToken)
	{
		var focusArea = AutoPilotPrompts.GetFocusArea(loop.CompletedIterations + loop.ConsecutiveIdeaMisses);
		var context = AutoPilotPrompts.BuildIdeaContext(focusArea, await GetRecentWorkAsync(loop.ProjectId, cancellationToken));

		var codingRequest = new SuggestIdeasRequest
		{
			UseInference = false,
			ProviderId = codingProvider.Id,
			ModelId = codingProvider.Id == loop.ProviderId ? loop.ModelId : null,
			IdeaCount = 1,
			AdditionalContext = context
		};

		SuggestIdeasResult result;
		if (loop.InferenceProviderId.HasValue)
		{
			result = await _ideaService.SuggestIdeasFromCodebaseAsync(loop.ProjectId, new SuggestIdeasRequest
			{
				UseInference = true,
				ProviderId = loop.InferenceProviderId,
				ModelId = loop.InferenceModelId,
				IdeaCount = 1,
				AdditionalContext = context
			}, cancellationToken);

			if (!result.Success && result.Stage != SuggestIdeasStage.RepoMapFailed)
			{
				_logger.LogWarning("Auto-pilot inference idea generation failed for project {ProjectId} ({Stage}: {Message}); asking {Provider} instead",
					loop.ProjectId, result.Stage, result.Message, codingProvider.Name);
				result = await _ideaService.SuggestIdeasFromCodebaseAsync(loop.ProjectId, codingRequest, cancellationToken);
			}
		}
		else
		{
			result = await _ideaService.SuggestIdeasFromCodebaseAsync(loop.ProjectId, codingRequest, cancellationToken);
		}

		if (result.Success && result.Ideas.Count > 0)
			return new IdeaResult(result.Ideas[0], result.Message);

		_logger.LogWarning("Auto-pilot got no new idea for project {ProjectId}: {Stage} - {Message}",
			loop.ProjectId, result.Stage, result.Message);

		if (result.Stage == SuggestIdeasStage.RepoMapFailed)
			return new IdeaResult(null, Truncate(result.Message, StatusMessageMaxLength), IsFatal: true);

		return new IdeaResult(null, result.Success
			? "The last suggestion repeated earlier work"
			: $"Couldn't get an idea: {result.Message}");
	}

	/// <summary>
	/// A round that produced nothing to build. It isn't a failure: the next round looks at the
	/// next focus area, and after a full round of them the loop rests before trying again.
	/// </summary>
	private async Task RecordIdeaMissAsync(IterationLoop loop, string reason, CancellationToken cancellationToken)
	{
		loop.ConsecutiveIdeaMisses++;
		reason = reason.TrimEnd('.', ' ');

		// Out of ideas with unpolished changes: polish them now rather than resting first.
		var fullRound = loop.ConsecutiveIdeaMisses % AutoPilotPrompts.FocusAreas.Count == 0;
		var rest = fullRound && !IsPolishDue(loop);

		loop.NextIterationAt = rest
			? DateTime.UtcNow.Add(NoIdeasRetryDelay)
			: DateTime.UtcNow.AddSeconds(loop.CooldownSeconds);
		loop.StatusMessage = Truncate(rest
			? $"No new ideas in any focus area, resting before the next round. {reason}."
			: $"{reason}. Looking at another area next.", StatusMessageMaxLength);

		await _dbContext.SaveChangesAsync(cancellationToken);
		await NotifyStateChanged(loop);
	}

	/// <summary>What auto-pilot already tried on this project, newest first, so ideas don't repeat.</summary>
	private async Task<List<AutoPilotWorkItem>> GetRecentWorkAsync(Guid projectId, CancellationToken cancellationToken)
	{
		var jobs = await _dbContext.Jobs
			.AsNoTracking()
			.Where(j => j.ProjectId == projectId
				&& j.IterationLoopId != null
				&& j.Title != null
				&& (j.Tags == null || !j.Tags.Contains(AutoPilotPrompts.PolishJobTag)))
			.OrderByDescending(j => j.CreatedAt)
			.Take(RecentWorkCount)
			.Select(j => new { j.Title, j.Status })
			.ToListAsync(cancellationToken);

		return jobs
			.Select(j => new AutoPilotWorkItem(j.Title!, j.Status == JobStatus.Completed))
			.ToList();
	}

	/// <summary>Deletes an auto-pilot idea that is still in the backlog after its job failed.</summary>
	private async Task RemoveLeftoverIdeaAsync(Guid ideaId, Guid? failedJobId, CancellationToken cancellationToken)
	{
		try
		{
			var leftover = await _dbContext.Ideas
				.AsNoTracking()
				.AnyAsync(i => i.Id == ideaId && (i.JobId == null || i.JobId == failedJobId), cancellationToken);
			if (leftover)
				await _ideaService.DeleteAsync(ideaId, cancellationToken);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Auto-pilot could not remove leftover idea {IdeaId}", ideaId);
		}
	}

	private async Task StopLoopAsync(IterationLoop loop, string reason, IterationLoopStatus status)
	{
		loop.Status = status;
		loop.StoppedAt = DateTime.UtcNow;
		loop.LastStopReason = Truncate(reason, StatusMessageMaxLength);
		loop.StatusMessage = null;
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

	private static string Truncate(string text, int maxLength) =>
		text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

	#endregion
}
