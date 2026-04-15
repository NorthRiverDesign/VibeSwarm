using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public static class AgentPresetHelper
{
	public static List<ProjectAgent> GetEnabledAgents(Project? project)
	{
		if (project?.AgentAssignments == null || project.AgentAssignments.Count == 0)
		{
			return [];
		}

		return project.AgentAssignments
			.Where(IsEnabledAgentAssignment)
			.OrderBy(assignment => assignment.Agent!.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public static ProjectAgent? ResolveAgent(Project? project, Guid? agentId)
	{
		if (!agentId.HasValue || agentId == Guid.Empty)
		{
			return null;
		}

		return GetEnabledAgents(project)
			.FirstOrDefault(assignment => assignment.AgentId == agentId.Value);
	}

	public static bool HasCustomCycleSettings(Job job)
	{
		return job.CycleMode != CycleMode.SingleCycle
			|| job.CycleSessionMode != CycleSessionMode.ContinueSession
			|| job.MaxCycles > 1
			|| !string.IsNullOrWhiteSpace(job.CycleReviewPrompt);
	}

	public static void ApplyExecutionDefaults(Job job, ProjectAgent assignment)
	{
		ArgumentNullException.ThrowIfNull(job);
		ArgumentNullException.ThrowIfNull(assignment);

		if (!IsEnabledAgentAssignment(assignment))
		{
			throw new InvalidOperationException("The selected agent is not enabled for this project.");
		}

		var agent = assignment.Agent!;
		job.AgentId = assignment.AgentId;

		if (job.ProviderId == Guid.Empty)
		{
			job.ProviderId = assignment.ProviderId;
		}
		else if (job.ProviderId != assignment.ProviderId)
		{
			throw new InvalidOperationException($"The selected agent '{agent.Name}' is assigned to a different provider.");
		}

		if (string.IsNullOrWhiteSpace(job.ModelUsed))
		{
			job.ModelUsed = string.IsNullOrWhiteSpace(assignment.PreferredModelId)
				? null
				: assignment.PreferredModelId.Trim();
		}

		if (string.IsNullOrWhiteSpace(job.ReasoningEffort))
		{
			job.ReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(assignment.PreferredReasoningEffort);
		}

		if (HasCustomCycleSettings(job))
		{
			return;
		}

		job.CycleMode = agent.DefaultCycleMode;
		job.CycleSessionMode = agent.DefaultCycleSessionMode;
		job.MaxCycles = Math.Clamp(agent.DefaultMaxCycles, 1, 100);
		job.CycleReviewPrompt = string.IsNullOrWhiteSpace(agent.DefaultCycleReviewPrompt)
			? null
			: agent.DefaultCycleReviewPrompt.Trim();
	}

	private static bool IsEnabledAgentAssignment(ProjectAgent assignment)
	{
		return assignment.IsEnabled
			&& assignment.ProviderId != Guid.Empty
			&& assignment.Agent != null
			&& assignment.Agent.IsEnabled;
	}
}
