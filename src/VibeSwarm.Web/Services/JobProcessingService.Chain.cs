using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// What a team job asks of the project's Lead. The Lead reads the repository read-only through
/// <see cref="IReadOnlyConsultService"/>, like the reviewer, and nothing it does can stop the job:
/// when it can't run, the builder carries on without it and the job says why.
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
					// Claude and Copilot take the same planning prompt.
					Prompt = ProviderPlanningHelper.BuildPlanningPrompt(ProviderType.Claude, job.GoalPrompt),
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

		job.PlanningOutput = plan;
		var planningSummary = JobSummaryGenerator.ExtractCommitSummary(plan);
		if (!string.IsNullOrWhiteSpace(planningSummary))
		{
			job.SessionSummary = planningSummary;
		}

		job.PlanningProviderId = project.LeadProviderId;
		job.PlanningModelUsed = result.ModelUsed ?? project.LeadModelId;
		job.PlanningReasoningEffortUsed = project.LeadReasoningEffort;
		job.PlanningGeneratedAt = DateTime.UtcNow;
		job.PlanningInputTokens = result.InputTokens;
		job.PlanningOutputTokens = result.OutputTokens;
		job.PlanningCostUsd = result.CostUsd;
		await dbContext.SaveChangesAsync(cancellationToken);
		return plan;
	}
}
