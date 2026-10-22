using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Team swarm members run at the same time, each in a git worktree of its own
/// (<see cref="SwarmWorktrees"/>). The project checkout is only touched under a per-project lock:
/// to prepare it before a run, and to replay a finished member's commits onto its branch. Work
/// that overlaps what another member delivered first stays on its branch for a merge job.
/// </summary>
public partial class JobProcessingService
{
	private const int MergeGoalPromptMaxLength = 2000;

	/// <summary>
	/// How often a member waiting for the checkout, or having its work checked and merged, tells
	/// the watchdog it is alive. Another member's checks can take longer than the stall timeout.
	/// </summary>
	private static readonly TimeSpan SwarmHeartbeatInterval = TimeSpan.FromMinutes(1);

	private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _checkoutLocks = new();

	/// <summary>Swarm members running in this process, by job id, and their worktrees.</summary>
	private readonly ConcurrentDictionary<Guid, string> _activeSwarmWorktrees = new();

	private bool RunsInSwarmWorktree(Job job) =>
		_swarmWorktrees != null
		&& job.SwarmId.HasValue
		&& string.IsNullOrWhiteSpace(job.MergeFromBranch)
		&& !LocalEnvironmentSetup.IsSetupJob(job);

	/// <summary>
	/// Where a job's run did its work: its swarm worktree when it has one, else the project checkout.
	/// </summary>
	private string? GetRunDirectory(Job job)
	{
		if (RunsInSwarmWorktree(job) && _swarmWorktrees!.GetPath(job.ProjectId, job.Id) is var worktree && Directory.Exists(worktree))
		{
			return worktree;
		}

		return job.Project?.WorkingPath;
	}

	/// <summary>
	/// Serializes git work on a project's checkout between the jobs of a swarm.
	/// </summary>
	private async Task<IDisposable> LockCheckoutAsync(Guid projectId, CancellationToken cancellationToken)
	{
		var gate = _checkoutLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(cancellationToken);
		return new CheckoutLock(gate);
	}

	private sealed class CheckoutLock(SemaphoreSlim gate) : IDisposable
	{
		private int _released;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _released, 1) == 0)
			{
				gate.Release();
			}
		}
	}

	/// <summary>
	/// Keeps the job's heartbeat fresh until disposed. Writes only the heartbeat, through a context
	/// of its own, so nothing the caller's context is still holding gets saved early.
	/// </summary>
	private IDisposable KeepJobAlive(Guid jobId)
	{
		var stop = new CancellationTokenSource();
		_ = Task.Run(async () =>
		{
			try
			{
				while (true)
				{
					await Task.Delay(SwarmHeartbeatInterval, stop.Token);
					using var scope = _scopeFactory.CreateScope();
					var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
					var now = DateTime.UtcNow;
					await dbContext.Jobs
						.Where(j => j.Id == jobId)
						.ExecuteUpdateAsync(setters => setters.SetProperty(j => j.LastHeartbeatAt, now), stop.Token);
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Stopped refreshing the heartbeat of job {JobId}", jobId);
			}
		});

		return new HeartbeatLease(stop);
	}

	private sealed class HeartbeatLease(CancellationTokenSource stop) : IDisposable
	{
		public void Dispose()
		{
			stop.Cancel();
			stop.Dispose();
		}
	}

	/// <summary>
	/// Shows what a finishing member is doing without saving the job, whose completion its caller
	/// is still putting together.
	/// </summary>
	private async Task ShowSwarmActivityAsync(Guid jobId, string activity, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
	{
		var now = DateTime.UtcNow;
		await dbContext.Jobs
			.Where(j => j.Id == jobId)
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(j => j.LastHeartbeatAt, now)
				.SetProperty(j => j.CurrentActivity, activity), cancellationToken);
		await NotifyJobActivityAsync(jobId, activity, now);
	}

	/// <summary>
	/// Gives a swarm member its own worktree, cut from the checkout the caller just prepared.
	/// Returns the directory the agent works in.
	/// </summary>
	private async Task<string> OpenSwarmWorktreeAsync(
		Job job,
		string checkout,
		JobExecutionContext executionContext,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		const string activity = "Setting up this agent's own copy of the repository...";
		await UpdateHeartbeatAsync(job.Id, activity, dbContext, cancellationToken);
		await NotifyJobActivityAsync(job.Id, activity, DateTime.UtcNow);

		SwarmWorktree worktree;
		try
		{
			worktree = await _swarmWorktrees!.OpenAsync(checkout, job.ProjectId, job.Id, cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			throw new GitPreparationException(ex.Message);
		}

		_activeSwarmWorktrees[job.Id] = worktree.Path;
		executionContext.SwarmCheckout = checkout;
		_logger.LogInformation("Job {JobId} works in swarm worktree {Worktree} on {Branch}{Reused}",
			job.Id, worktree.Path, worktree.Branch, worktree.Reused ? " (kept from its last run)" : "");
		return worktree.Path;
	}

	/// <summary>
	/// Releases worktrees no live job owns any more, such as one kept for a paused member that was
	/// cancelled before it resumed. Their work goes to branches, never away.
	/// </summary>
	private async Task SweepSwarmWorktreesAsync(Job job, string checkout, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
	{
		if (_swarmWorktrees == null)
		{
			return;
		}

		try
		{
			var liveJobIds = await dbContext.Jobs
				.AsNoTracking()
				.Where(j => j.ProjectId == job.ProjectId
					&& j.Status != JobStatus.Completed
					&& j.Status != JobStatus.Failed
					&& j.Status != JobStatus.Cancelled)
				.Select(j => j.Id)
				.ToListAsync(cancellationToken);
			liveJobIds.Add(job.Id);
			liveJobIds.AddRange(_activeSwarmWorktrees.Keys);

			foreach (var branch in await _swarmWorktrees.SweepAsync(checkout, job.ProjectId, liveJobIds, cancellationToken))
			{
				_logger.LogWarning("Kept the work of a finished agent's worktree in project {ProjectId} on branch {Branch}", job.ProjectId, branch);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogWarning(ex, "Could not tidy up finished swarm worktrees for job {JobId}", job.Id);
		}
	}

	/// <summary>
	/// The end of a swarm member's run, in place of the usual delivery. A completed run is
	/// verified and committed in its worktree, then replayed onto the project's branch and pushed
	/// per the project's settings. Members do this one at a time, so each lands on top of the
	/// last and the checks of two members never compete for the CPU.
	/// </summary>
	private async Task FinishSwarmRunAsync(
		Job job,
		JobStatus status,
		string checkout,
		string worktree,
		JobExecutionContext executionContext,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		if (status != JobStatus.Completed)
		{
			// The worktree itself is released once the run is over (ReleaseSwarmWorktreeAfterRunAsync).
			job.WorkSnapshotCommit = await SaveRunWorkSnapshotAsync(job, worktree, executionContext.GitCommitBefore, cancellationToken);
			return;
		}

		using var heartbeat = KeepJobAlive(job.Id);
		await ShowSwarmActivityAsync(job.Id, "Waiting for the other agents' work to land...", dbContext, cancellationToken);
		using var checkoutLock = await LockCheckoutAsync(job.ProjectId, cancellationToken);
		await ShowSwarmActivityAsync(job.Id, "Checking this agent's work...", dbContext, cancellationToken);

		var verified = await VerifyRestoreConflictsResolvedAsync(job, worktree, executionContext.PriorWorkRestore, cancellationToken)
			&& await VerifyBuildAsync(job, worktree, cancellationToken);
		if (verified)
		{
			var enableCommitAttribution = await dbContext.AppSettings
				.OrderBy(settings => settings.Id)
				.Select(settings => (bool?)settings.EnableCommitAttribution)
				.FirstOrDefaultAsync(cancellationToken)
				?? true;

			// Committed even when the project leaves commits to the user: agents working side by
			// side can only be combined as commits. Pushing waits until the work is on the branch.
			await CommitJobWorkAsync(job, worktree, enableCommitAttribution, push: false, cancellationToken);
		}

		// Saved before the replay rewrites the worktree's commits, so it holds this run's work only.
		job.WorkSnapshotCommit = await SaveRunWorkSnapshotAsync(job, worktree, executionContext.GitCommitBefore, cancellationToken);

		var integration = verified && !string.IsNullOrWhiteSpace(job.GitCommitHash)
			? await IntegrateSwarmWorkAsync(job, checkout, worktree, dbContext, cancellationToken)
			: null;

		_activeSwarmWorktrees.TryRemove(job.Id, out _);
		var keptBranch = await _swarmWorktrees!.ReleaseAsync(checkout, worktree, job.Id.ToString("N")[..8], cancellationToken);
		if (keptBranch == null)
		{
			return;
		}

		if (integration?.Outcome == SwarmIntegrationOutcome.Conflicted)
		{
			await QueueSwarmMergeJobAsync(job, keptBranch, integration.ConflictedFiles, dbContext);
			return;
		}

		job.ErrorMessage = CombineNotices(job.ErrorMessage, integration?.Outcome == SwarmIntegrationOutcome.Failed
			? $"Its work couldn't be added to the branch ({integration.Error}), so it is kept on the branch {keptBranch}."
			: $"Its work is kept on the branch {keptBranch}.");
	}

	/// <summary>
	/// Brings the checkout up to date (other members and origin may have moved the branch on since
	/// this member started), replays the member's commits on top, and delivers them.
	/// </summary>
	private async Task<SwarmIntegration> IntegrateSwarmWorkAsync(
		Job job,
		string checkout,
		string worktree,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		await ShowSwarmActivityAsync(job.Id, "Adding this agent's work to the branch...", dbContext, cancellationToken);

		SwarmIntegration integration;
		try
		{
			var checkpointBaseBranch = await PreserveWorkingTreeBeforeBranchPreparationAsync(
				job,
				checkout,
				dbContext,
				captureJobDiff: false,
				reason: "Protected local changes before adding an agent's work to the branch.",
				cancellationToken: cancellationToken);
			await PrepareWorkingBranchAsync(job, checkout, checkpointBaseBranch, cancellationToken);
			integration = await _swarmWorktrees!.IntegrateAsync(checkout, worktree, cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			integration = SwarmIntegration.Failed(ex.Message);
		}

		if (integration.Outcome != SwarmIntegrationOutcome.Integrated)
		{
			_logger.LogWarning("Could not add the work of swarm job {JobId} to the branch: {Outcome} {Error} {Files}",
				job.Id, integration.Outcome, integration.Error, string.Join(", ", integration.ConflictedFiles));
			return integration;
		}

		job.GitCommitHash = integration.CommitHash;
		_logger.LogInformation("Added the work of swarm job {JobId} to the branch at {Commit}",
			job.Id, integration.CommitHash?[..Math.Min(8, integration.CommitHash.Length)]);

		if (GetEffectiveCommitMode(job) == AutoCommitMode.CommitAndPush)
		{
			await PushJobCommitAsync(job, checkout, cancellationToken);
		}

		await CreatePullRequestIfConfiguredAsync(job, checkout, cancellationToken);
		return integration;
	}

	/// <summary>
	/// Hands work that overlaps another member's to a job that merges its branch in and resolves
	/// the conflicts, run by the same provider once the rest of the swarm has finished.
	/// </summary>
	private async Task QueueSwarmMergeJobAsync(Job job, string branch, IReadOnlyList<string> conflictedFiles, VibeSwarmDbContext dbContext)
	{
		try
		{
			var agentName = job.AgentId.HasValue
				? await dbContext.Agents.AsNoTracking().Where(agent => agent.Id == job.AgentId).Select(agent => agent.Name).FirstOrDefaultAsync()
				: null;

			Job BuildMergeJob(bool sameProvider) => new()
			{
				ProjectId = job.ProjectId,
				ProviderId = sameProvider ? job.ProviderId : Guid.Empty,
				ModelUsed = sameProvider ? job.ModelUsed : null,
				ReasoningEffort = sameProvider ? job.ReasoningEffort : null,
				Title = $"Merge {agentName ?? "an agent"}'s work: {job.Title ?? job.GoalPrompt}",
				GoalPrompt = BuildSwarmMergeGoalPrompt(agentName, job.GoalPrompt, branch, conflictedFiles),
				Branch = job.Branch,
				TargetBranch = job.TargetBranch,
				MergeFromBranch = branch,
				GitChangeDeliveryMode = job.GitChangeDeliveryMode,
				CommitModeOverride = GetEffectiveCommitMode(job),
				Priority = job.Priority
			};

			using var scope = _scopeFactory.CreateScope();
			var jobService = scope.ServiceProvider.GetRequiredService<IJobService>();
			Job mergeJob;
			try
			{
				mergeJob = await jobService.CreateAsync(BuildMergeJob(sameProvider: true), CancellationToken.None);
			}
			catch (InvalidOperationException ex)
			{
				// The member's provider may have been disabled since; any project provider can merge.
				_logger.LogInformation("Queuing the merge for swarm job {JobId} on another provider: {Error}", job.Id, ex.Message);
				mergeJob = await jobService.CreateAsync(BuildMergeJob(sameProvider: false), CancellationToken.None);
			}

			job.ErrorMessage = CombineNotices(job.ErrorMessage,
				$"Its changes overlap work another agent added first, so they are kept on the branch {branch} and job {mergeJob.Id.ToString("N")[..8]} merges them in.");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not queue a merge job for swarm job {JobId}", job.Id);
			job.ErrorMessage = CombineNotices(job.ErrorMessage,
				$"Its changes overlap work another agent added first, so they are kept on the branch {branch}. Merge that branch to finish.");
		}
	}

	internal static string BuildSwarmMergeGoalPrompt(string? agentName, string teamGoal, string branch, IReadOnlyList<string> conflictedFiles)
	{
		var agent = string.IsNullOrWhiteSpace(agentName) ? "One agent" : $"The {agentName} agent";
		var intro =
			$"A team of agents worked on the goal below at the same time, each in its own copy of the repository. " +
			$"{agent}'s changes overlap work another agent added to the branch first, so they could not be combined automatically. " +
			$"Merge the branch '{branch}' into the current branch and resolve every conflict so that both sides' changes are kept and work together. " +
			"VibeSwarm starts the merge before you begin.";
		var files = conflictedFiles.Count == 0
			? ""
			: $"{Environment.NewLine}{Environment.NewLine}Conflicted files:{Environment.NewLine}" +
				string.Join(Environment.NewLine, conflictedFiles.Take(20).Select(file => $"- {file}"));
		var goalHeading = $"{Environment.NewLine}{Environment.NewLine}The team's goal:{Environment.NewLine}";
		var goalRoom = Math.Max(0, MergeGoalPromptMaxLength - intro.Length - files.Length - goalHeading.Length);
		var goal = teamGoal.Trim();
		if (goal.Length > goalRoom)
		{
			goal = goalRoom > 1 ? goal[..(goalRoom - 1)] + "…" : "";
		}

		var prompt = goal.Length == 0 ? intro + files : intro + goalHeading + goal + files;
		return prompt.Length <= MergeGoalPromptMaxLength ? prompt : prompt[..MergeGoalPromptMaxLength];
	}

	/// <summary>
	/// Once a swarm member's run is over, releases its worktree if the job is finished. The work
	/// goes to the member's branch when it isn't on the project's branch already. A job going back
	/// in the queue, or paused, keeps its worktree for the next run.
	/// </summary>
	private async Task ReleaseSwarmWorktreeAfterRunAsync(Guid jobId, JobExecutionContext executionContext)
	{
		if (!_activeSwarmWorktrees.TryRemove(jobId, out var worktree) ||
			executionContext.SwarmCheckout is not { } checkout ||
			!Directory.Exists(worktree))
		{
			return;
		}

		try
		{
			using var scope = _scopeFactory.CreateScope();
			var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == jobId);
			if (job != null && job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
			{
				return;
			}

			using var checkoutLock = await LockCheckoutAsync(executionContext.ProjectId, CancellationToken.None);
			var keptBranch = await _swarmWorktrees!.ReleaseAsync(checkout, worktree, jobId.ToString("N")[..8], CancellationToken.None);
			if (keptBranch != null && job != null)
			{
				job.ErrorMessage = CombineNotices(job.ErrorMessage, $"Its work is kept on the branch {keptBranch}.");
				await dbContext.SaveChangesAsync();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not release the swarm worktree of job {JobId}; it stays at {Worktree}", jobId, worktree);
		}
	}
}
