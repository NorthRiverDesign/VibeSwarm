using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public static class ProjectSkillHelper
{
	public static IReadOnlyList<Skill> GetConfiguredSkills(Project? project)
	{
		if (project?.AgentAssignments == null || project.AgentAssignments.Count == 0)
		{
			return [];
		}

		return project.AgentAssignments
			.Where(assignment => assignment.IsEnabled && assignment.Agent?.IsEnabled == true)
			.SelectMany(assignment => assignment.Agent!.SkillLinks)
			.Where(link => link.Skill != null && link.Skill.IsEnabled)
			.Select(link => link.Skill!)
			.GroupBy(skill => skill.Id)
			.Select(group => group.First())
			.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public static IReadOnlyList<Skill> GetExecutionSkills(Project? project, IEnumerable<Skill> enabledSkills)
	{
		var enabledSkillList = enabledSkills
			.Where(skill => skill.IsEnabled)
			.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (project == null)
		{
			return enabledSkillList;
		}

		if (project.AgentAssignments == null || project.AgentAssignments.Count == 0)
		{
			return [];
		}

		var enabledSkillsById = enabledSkillList.ToDictionary(skill => skill.Id);
		var selectedSkills = new Dictionary<Guid, Skill>();

		foreach (var assignment in project.AgentAssignments.Where(assignment => assignment.IsEnabled && assignment.Agent?.IsEnabled == true))
		{
			foreach (var link in assignment.Agent!.SkillLinks)
			{
				if (enabledSkillsById.TryGetValue(link.SkillId, out var skill))
				{
					selectedSkills[skill.Id] = skill;
				}
			}
		}

		return selectedSkills.Values
			.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}
}
