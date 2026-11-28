using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Web.Services;

/// <summary>
/// A team job's chain. The project's Lead plans the job into steps, reading the repository
/// read-only through <see cref="IReadOnlyConsultService"/> like the reviewer; the builder then
/// carries the steps out one at a time, each in a session of its own, inside the run loop in
/// <c>ProcessJobAsync</c>. Nothing the Lead does can stop the job: when it can't plan, the builder
/// works from the goal and the job says why.
/// </summary>
public partial class JobProcessingService
{
	/// <summary>How long the Lead may take to plan a job before the builder starts without a plan.</summary>
	private static readonly TimeSpan LeadPlanTimeout = TimeSpan.FromMinutes(30);

	/// <summary>
	/// Whether the job still waits for the Lead's plan: a team job on a project with a Lead that
	/// has neither a plan nor a recorded reason for going without one.
	/// </summary>
	internal static bool NeedsLeadPlan(Job job) =>
		job.TeamRun
		&& job.Project is { LeadProviderId: not null }
		&& string.IsNullOrWhiteSpace(job.PlanningOutput)
		&& string.IsNullOrWhiteSpace(job.PlanSkippedReason);

	/// <summary>
	/// Asks the project's Lead to plan the job and records the answer on it. Returns the plan,
	/// or null when the Lead couldn't give one; the job then keeps the reason in
	/// <see cref="Job.PlanSkippedReason"/> and is built from its goal alone.
	/// </summary>
	internal async Task<string?> PlanWithLeadAsync(
		Job job,
		string? workingDirectory,
		string? systemPrompt,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		var project = job.Project!;
		ConsultResult result;
		using (var scope = _scopeFactory.CreateScope())
		{
			var consult = scope.ServiceProvider.GetService<IReadOnlyConsultService>();
			result = consult == null
				? ConsultResult.Skip("No Lead is available")
				: await consult.ConsultAsync(new ConsultRequest
				{
					Target = new ConsultTarget(project.LeadProviderId, null, project.LeadModelId, project.LeadReasoningEffort),
					Prompt = ChainPrompts.BuildPlanPrompt(job.GoalPrompt),
					SystemPrompt = systemPrompt,
					WorkingDirectory = workingDirectory,
					JobId = job.Id,
					Title = string.IsNullOrWhiteSpace(job.Title) ? "Plan" : $"Plan: {job.Title}",
					TaskType = "plan",
					Timeout = LeadPlanTimeout
				}, cancellationToken);
		}

		cancellationToken.ThrowIfCancellationRequested();

		var plan = result.Success ? result.Text?.Trim() : null;
		if (string.IsNullOrWhiteSpace(plan))
		{
			var reason = string.IsNullOrWhiteSpace(result.Error) ? "The Lead gave no plan" : result.Error.Trim();
			job.PlanSkippedReason = reason.Length > 500 ? reason[..500] : reason;
			_logger.LogWarning("Job {JobId} is built without a plan: {Reason}", job.Id, job.PlanSkippedReason);
			await dbContext.SaveChangesAsync(cancellationToken);
			return null;
		}

		// The brief is what every step is given; the steps follow any the job already has, which
		// are the record of an earlier run.
		var parsed = ChainPrompts.ParsePlan(plan, job.GoalPrompt);
		job.PlanningOutput = parsed.Brief;
		var lastOrder = await dbContext.JobSteps
			.Where(step => step.JobId == job.Id)
			.Select(step => (int?)step.Order)
			.MaxAsync(cancellationToken) ?? 0;
		foreach (var planned in parsed.Steps)
		{
			dbContext.JobSteps.Add(new JobStep
			{
				JobId = job.Id,
				Order = ++lastOrder,
				Title = planned.Title,
				Instructions = planned.Instructions,
				DoneWhen = planned.DoneWhen,
				Source = JobStepSource.Lead
			});
		}

		job.PlanningProviderId = project.LeadProviderId;
		job.PlanningModelUsed = result.ModelUsed ?? project.LeadModelId;
		job.PlanningReasoningEffortUsed = project.LeadReasoningEffort;
		job.PlanningGeneratedAt = DateTime.UtcNow;
		job.PlanningInputTokens = result.InputTokens;
		job.PlanningOutputTokens = result.OutputTokens;
		job.PlanningCostUsd = result.CostUsd;
		await dbContext.SaveChangesAsync(cancellationToken);
		await NotifyStepsChangedAsync(job.Id);
		return parsed.Brief;
	}

	/// <summary>
	/// The step a run of the job starts on, or null when the job has no steps and is built in one
	/// go. A step interrupted mid-way is handed back as it is, for the run to resume; any other
	/// run starts the first step still waiting.
	/// </summary>
	private async Task<JobStep?> BeginChainAsync(
		Job job,
		JobExecutionContext executionContext,
		bool resuming,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		if (!job.TeamRun)
		{
			return null;
		}

		var steps = await LoadStepsAsync(dbContext, job.Id, cancellationToken);
		if (steps.Count == 0)
		{
			return null;
		}

		// A step is Done only while its work is in the tree this run starts from. Every run
		// resets the checkout, and only a resumed run gets the job's saved work back; without it
		// the steps are built again rather than skipped over an empty tree.
		var builtSteps = steps.Where(step => step.Status is JobStepStatus.Done or JobStepStatus.Running).ToList();
		var savedWorkIsBack = resuming && executionContext.PriorWorkRestore is { Outcome: not JobWorkRestoreOutcome.Failed };
		var nothingWasSaved = builtSteps.All(step => step.SnapshotCommit == null) && string.IsNullOrWhiteSpace(job.WorkSnapshotCommit);
		if (builtSteps.Count > 0 && !savedWorkIsBack && !(resuming && nothingWasSaved))
		{
			foreach (var step in builtSteps)
			{
				step.Status = JobStepStatus.Waiting;
				step.Summary = null;
				step.ReviewStatus = JobReviewStatus.None;
				step.ReviewRounds = 0;
				step.ReviewNotes = null;
				step.SessionId = null;
				step.SnapshotCommit = null;
				step.StartedAt = null;
				step.CompletedAt = null;
			}

			await dbContext.SaveChangesAsync(cancellationToken);
			await NotifyStepsChangedAsync(job.Id);
		}

		return steps.FirstOrDefault(step => step.Status == JobStepStatus.Running)
			?? steps.FirstOrDefault(step => step.Status == JobStepStatus.Waiting);
	}

	/// <summary>
	/// Marks the step as the one being built and gives it a clean slate: the job's review state
	/// is the step's while it runs, and its session is its own, so a crash can't resume the
	/// session of the step before it.
	/// </summary>
	internal async Task StartStepAsync(Job job, JobStep step, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
	{
		step.Status = JobStepStatus.Running;
		step.StartedAt = DateTime.UtcNow;
		step.CompletedAt = null;
		job.SessionId = null;
		JobReview.Reset(job);
		await dbContext.SaveChangesAsync(cancellationToken);

		// The review state is also written straight to the row (SaveReviewAsync does), so clear it there too.
		using (var scope = _scopeFactory.CreateScope())
		{
			var reviewDb = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			await reviewDb.Jobs
				.Where(j => j.Id == job.Id)
				.ExecuteUpdateAsync(setters => setters
					.SetProperty(j => j.ReviewStatus, JobReviewStatus.None)
					.SetProperty(j => j.ReviewRounds, 0)
					.SetProperty(j => j.ReviewNotes, (string?)null)
					.SetProperty(j => j.BuildCheckedTreeHash, (string?)null),
					CancellationToken.None);
		}

		await NotifyStepsChangedAsync(job.Id);
	}

	/// <summary>
	/// Closes the step the builder just finished: keeps its handoff note and what the reviewer
	/// made of it, and saves the work so the next step, or a resumed run, starts from it.
	/// Returns the step to build next, or null when none is waiting.
	/// </summary>
	internal async Task<JobStep?> FinishStepAsync(
		Job job,
		JobStep step,
		ExecutionResult result,
		string? workingDirectory,
		string? baseCommit,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		step.Status = JobStepStatus.Done;
		step.CompletedAt = DateTime.UtcNow;
		step.Summary = ChainPrompts.ExtractHandoff(ProviderPlanningHelper.ExtractExecutionText(result));
		step.SessionId = result.SessionId ?? job.SessionId;
		step.ReviewStatus = job.ReviewStatus;
		step.ReviewRounds = job.ReviewRounds;
		step.ReviewNotes = job.ReviewNotes;

		if (!string.IsNullOrEmpty(workingDirectory))
		{
			var snapshot = await SaveRunWorkSnapshotAsync(job, workingDirectory, baseCommit, cancellationToken);
			if (snapshot != null)
			{
				step.SnapshotCommit = snapshot;
				job.WorkSnapshotCommit = snapshot;
			}
		}

		await dbContext.SaveChangesAsync(cancellationToken);
		await NotifyStepsChangedAsync(job.Id);

		// Read afresh: a step may have been added or skipped from the job's page meanwhile.
		return await dbContext.JobSteps
			.Where(next => next.JobId == job.Id && next.Status == JobStepStatus.Waiting)
			.OrderBy(next => next.Order)
			.FirstOrDefaultAsync(cancellationToken);
	}

	/// <summary>
	/// What running the step takes: its prompt (the job's own prompt, then the brief, the list of
	/// steps and the step), the commit its change is measured from, and the reviewer's scope.
	/// </summary>
	internal async Task<StepRun> DescribeStepAsync(
		Job job,
		string jobPrompt,
		JobStep step,
		string? jobBaseCommit,
		VibeSwarmDbContext dbContext,
		CancellationToken cancellationToken)
	{
		var steps = await LoadStepsAsync(dbContext, job.Id, cancellationToken);
		var counted = steps.Where(other => other.Status != JobStepStatus.Skipped || other.Id == step.Id).ToList();
		var position = counted.FindIndex(other => other.Id == step.Id) + 1;

		// The step's change is what differs from the step before it; one that saved nothing
		// leaves the base where it was.
		var baseCommit = steps
			.Where(other => other.Order < step.Order && other.Status == JobStepStatus.Done && other.SnapshotCommit != null)
			.OrderByDescending(other => other.Order)
			.Select(other => other.SnapshotCommit)
			.FirstOrDefault() ?? jobBaseCommit;

		return new StepRun(
			$"{jobPrompt.TrimEnd()}\n\n{ChainPrompts.BuildStepSection(job.PlanningOutput, steps, step)}",
			baseCommit,
			ScopeReviewToStep(step, position, counted.Count),
			position,
			counted.Count);
	}

	/// <summary>
	/// Sums the steps' reviews up on the job, which its page and the Lead read: every fix round
	/// counted, and the findings of any step whose review ran out of rounds.
	/// </summary>
	internal async Task SummariseStepReviewsAsync(Job job, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
	{
		var steps = (await LoadStepsAsync(dbContext, job.Id, cancellationToken))
			.Where(step => step.Status == JobStepStatus.Done)
			.ToList();
		if (steps.Count == 0)
		{
			return;
		}

		job.ReviewRounds = steps.Sum(step => step.ReviewRounds);
		var unresolved = steps.Where(step => step.ReviewStatus == JobReviewStatus.Unresolved).ToList();
		if (unresolved.Count > 0)
		{
			var notes = string.Join("\n\n", unresolved.Select(step => $"Step {step.Order}, {step.Title}:\n{step.ReviewNotes}"));
			await SaveReviewAsync(job, JobReviewStatus.Unresolved, notes, cancellationToken);
		}
		else if (steps.Any(step => step.ReviewStatus == JobReviewStatus.Approved))
		{
			await SaveReviewAsync(job, JobReviewStatus.Approved, null, cancellationToken);
		}
		else
		{
			// Nothing was reviewed: the job keeps whatever the last step recorded, such as why not.
			await SaveReviewAsync(job, job.ReviewStatus, job.ReviewNotes, cancellationToken);
		}
	}

	/// <summary>What the reviewer is told when it judges one step rather than the whole job.</summary>
	internal static ReviewScope ScopeReviewToStep(JobStep step, int position, int count)
	{
		var goal = string.IsNullOrWhiteSpace(step.DoneWhen)
			? $"{step.Title}\n{step.Instructions}"
			: $"{step.Title}\n{step.Instructions}\nDone when: {step.DoneWhen}";
		return new ReviewScope(goal, $"This change is step {position} of {count} of a larger job. Judge only this step: later steps cover the rest of the job, so work that belongs to them is not missing.");
	}

	private static Task<List<JobStep>> LoadStepsAsync(VibeSwarmDbContext dbContext, Guid jobId, CancellationToken cancellationToken)
		=> dbContext.JobSteps.Where(step => step.JobId == jobId).OrderBy(step => step.Order).ToListAsync(cancellationToken);

	private async Task NotifyStepsChangedAsync(Guid jobId)
	{
		if (_jobUpdateService == null)
		{
			return;
		}

		try
		{
			await _jobUpdateService.NotifyJobStepsChanged(jobId);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Could not announce a step change for job {JobId}", jobId);
		}
	}
}

/// <summary>Narrows a review to one step: what the step was to do, and a note on what lies outside it.</summary>
internal sealed record ReviewScope(string Goal, string Note);

/// <summary>What a step's run needs: its prompt, the commit its change is measured from, and where it sits.</summary>
internal sealed record StepRun(string Prompt, string? BaseCommit, ReviewScope Scope, int Position, int Count);
