using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public static class AgentPresetHelper
{
	public static List<ProjectTeamRole> GetEnabledAgents(Project? project)
	{
		if (project?.TeamAssignments == null || project.TeamAssignments.Count == 0)
		{
			return [];
		}

		return project.TeamAssignments
			.Where(IsEnabledAgentAssignment)
			.OrderBy(assignment => assignment.TeamRole!.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public static ProjectTeamRole? ResolveAgent(Project? project, Guid? teamRoleId)
	{
		if (!teamRoleId.HasValue || teamRoleId == Guid.Empty)
		{
			return null;
		}

		return GetEnabledAgents(project)
			.FirstOrDefault(assignment => assignment.TeamRoleId == teamRoleId.Value);
	}

	public static bool HasCustomCycleSettings(Job job)
	{
		return job.CycleMode != CycleMode.SingleCycle
			|| job.CycleSessionMode != CycleSessionMode.ContinueSession
			|| job.MaxCycles > 1
			|| !string.IsNullOrWhiteSpace(job.CycleReviewPrompt);
	}

	public static void ApplyExecutionDefaults(Job job, ProjectTeamRole assignment)
	{
		ArgumentNullException.ThrowIfNull(job);
		ArgumentNullException.ThrowIfNull(assignment);

		if (!IsEnabledAgentAssignment(assignment))
		{
			throw new InvalidOperationException("The selected agent is not enabled for this project.");
		}

		var teamRole = assignment.TeamRole!;
		job.TeamRoleId = assignment.TeamRoleId;

		if (job.ProviderId == Guid.Empty)
		{
			job.ProviderId = assignment.ProviderId;
		}
		else if (job.ProviderId != assignment.ProviderId)
		{
			throw new InvalidOperationException($"The selected agent '{teamRole.Name}' is assigned to a different provider.");
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

		job.CycleMode = teamRole.DefaultCycleMode;
		job.CycleSessionMode = teamRole.DefaultCycleSessionMode;
		job.MaxCycles = Math.Clamp(teamRole.DefaultMaxCycles, 1, 100);
		job.CycleReviewPrompt = string.IsNullOrWhiteSpace(teamRole.DefaultCycleReviewPrompt)
			? null
			: teamRole.DefaultCycleReviewPrompt.Trim();
	}

	private static bool IsEnabledAgentAssignment(ProjectTeamRole assignment)
	{
		return assignment.IsEnabled
			&& assignment.ProviderId != Guid.Empty
			&& assignment.TeamRole != null
			&& assignment.TeamRole.IsEnabled;
	}
}
