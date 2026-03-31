using System.Text;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Builds structured XML-tagged prompts for CLI agents to improve task clarity and reduce wasted tokens.
/// Wraps raw user prompts with project context, constraints, and structured formatting.
/// </summary>
public static class PromptBuilder
{
	private const int MaxPromptLength = 2000;
	private const int XmlOverhead = 200;
	private const int MaxEnvironmentSectionLength = 1200;

	public static string BuildStructuredPrompt(Job job, bool enableStructuring = true)
	{
		if (job == null)
		{
			return string.Empty;
		}

		if (!enableStructuring || job.Project == null)
		{
			return job.GoalPrompt;
		}

		var environmentSection = BuildEnvironmentSection(job.Project);
		var teamSection = BuildTeamSection(job.Project);
		var sb = new StringBuilder();

		sb.AppendLine("<task>");
		sb.AppendLine(job.GoalPrompt.Trim());
		sb.AppendLine("</task>");

		sb.AppendLine("<project>");
		sb.AppendLine($"  <name>{EscapeXml(job.Project.Name)}</name>");
		if (!string.IsNullOrWhiteSpace(job.Project.Description))
		{
			sb.AppendLine($"  <description>{EscapeXml(job.Project.Description)}</description>");
		}
		sb.AppendLine("</project>");

		if (!string.IsNullOrWhiteSpace(environmentSection))
		{
			sb.Append(environmentSection);
		}

		if (!string.IsNullOrWhiteSpace(teamSection))
		{
			sb.Append(teamSection);
		}

		var hasConstraints = !string.IsNullOrWhiteSpace(job.Project.PromptContext)
			|| job.MaxCostUsd.HasValue
			|| !string.IsNullOrWhiteSpace(job.Branch)
			|| !string.IsNullOrWhiteSpace(job.TargetBranch)
			|| job.GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest;

		if (hasConstraints)
		{
			sb.AppendLine("<constraints>");

			if (!string.IsNullOrWhiteSpace(job.Project.PromptContext))
			{
				var context = job.Project.PromptContext.Trim();
				var availableSpace = MaxPromptLength - XmlOverhead - job.GoalPrompt.Length - environmentSection.Length - teamSection.Length - 100;
				if (availableSpace > 0 && context.Length > availableSpace)
				{
					context = context[..availableSpace] + "...";
				}
				sb.AppendLine($"  {context}");
			}

			if (job.MaxCostUsd.HasValue)
			{
				sb.AppendLine($"  Maximum budget: ${job.MaxCostUsd.Value:F2} USD");
			}

			if (!string.IsNullOrWhiteSpace(job.Branch))
			{
				sb.AppendLine($"  Working branch: {job.Branch}");
			}

			if (!string.IsNullOrWhiteSpace(job.TargetBranch))
			{
				sb.AppendLine($"  Delivery target branch: {job.TargetBranch}");
			}

			if (job.GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest)
			{
				sb.AppendLine("  Deliver changes through a pull request instead of leaving them only on the working branch.");
			}

			sb.AppendLine("  Only modify files directly related to the task.");
			sb.AppendLine("  Do not refactor unrelated code.");
			sb.AppendLine("</constraints>");
		}

		var result = sb.ToString().TrimEnd();
		return result.Length > MaxPromptLength ? job.GoalPrompt : result;
	}

	public static string BuildExecutionPrompt(Job job, string? planningOutput, bool enableStructuring = true)
	{
		var basePrompt = BuildStructuredPrompt(job, enableStructuring);
		if (string.IsNullOrWhiteSpace(planningOutput))
		{
			return basePrompt;
		}

		var sb = new StringBuilder();
		sb.AppendLine(basePrompt);
		sb.AppendLine();
		sb.AppendLine("<implementation_plan>");
		sb.AppendLine(planningOutput.Trim());
		sb.AppendLine("</implementation_plan>");
		sb.AppendLine();
		sb.AppendLine("Use the implementation plan above as the approved plan for this task.");
		sb.AppendLine("Execute the work now. Do not spend time generating another plan unless the task reveals missing information.");
		return sb.ToString().TrimEnd();
	}

	public static string? BuildSystemPromptRules(
		Project? project,
		bool injectEfficiencyRules = true,
		bool injectRepoMap = true,
		ProviderType? providerType = null,
		bool enableCommitAttribution = true)
	{
		if (project == null)
		{
			return null;
		}

		var sb = new StringBuilder();

		if (injectEfficiencyRules)
		{
			sb.AppendLine("IMPORTANT RULES:");
			sb.AppendLine("- Only perform the requested task. Do not modify unrelated files.");
			sb.AppendLine("- Do not add comments, docstrings, or type annotations to code you did not change.");
			sb.AppendLine("- Do not refactor or \"improve\" code beyond what was requested.");
			sb.AppendLine("- If you encounter issues unrelated to the task, note them but do not fix them.");
			sb.AppendLine();
			sb.AppendLine("BUILD VERIFICATION (CRITICAL):");
			sb.AppendLine("- You MUST verify that your changes compile and build successfully before finishing.");

			if (!string.IsNullOrWhiteSpace(project.BuildCommand))
			{
				sb.AppendLine($"- Run the project build command: {project.BuildCommand.Trim()}");
			}
			else
			{
				sb.AppendLine("- Run the appropriate build command for this project (e.g., dotnet build, npm run build, cargo build).");
			}

			if (!string.IsNullOrWhiteSpace(project.TestCommand))
			{
				sb.AppendLine($"- Run the project test command: {project.TestCommand.Trim()}");
			}

			sb.AppendLine("- If the build or tests fail, fix the issues before completing your work.");
			sb.AppendLine("- Never leave the project in a broken state. A failing build is unacceptable.");

			var commitAttributionRules = CommitAttributionHelper.BuildPromptRules(providerType, enableCommitAttribution);
			if (commitAttributionRules.Count > 0)
			{
				sb.AppendLine();
				sb.AppendLine("COMMIT ATTRIBUTION:");
				foreach (var rule in commitAttributionRules)
				{
					sb.AppendLine($"- {rule}");
				}
			}
		}

		var enabledEnvironments = project.Environments
			.Where(environment => environment.IsEnabled)
			.ToList();

		if (enabledEnvironments.Any(environment => environment.Type == EnvironmentType.Web))
		{
			if (sb.Length > 0)
			{
				sb.AppendLine();
			}

			sb.AppendLine("DEPLOYED ENVIRONMENTS:");
			sb.AppendLine("- Use Playwright MCP when browser interaction is needed against configured web environments.");
			sb.AppendLine("- Do not assume localhost when a project environment URL is available.");
			sb.AppendLine("- Web environments may lag behind repository changes until a deployment or restart occurs.");
		}

		if (enabledEnvironments.Count > 0)
		{
			var environmentStageRules = BuildEnvironmentStageRules(enabledEnvironments);
			if (environmentStageRules.Count > 0)
			{
				if (sb.Length > 0)
				{
					sb.AppendLine();
				}

				sb.AppendLine("ENVIRONMENT SAFETY:");
				foreach (var rule in environmentStageRules)
				{
					sb.AppendLine($"- {rule}");
				}
			}
		}

		if (injectRepoMap && !string.IsNullOrWhiteSpace(project.RepoMap))
		{
			if (sb.Length > 0)
			{
				sb.AppendLine();
			}

			sb.AppendLine("PROJECT STRUCTURE:");
			sb.AppendLine(project.RepoMap.Trim());
		}

		return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
	}

	public static string? BuildProjectMemoryRules(Project? project, string? memoryFilePath)
	{
		if (project == null || string.IsNullOrWhiteSpace(memoryFilePath))
		{
			return null;
		}

		var sb = new StringBuilder();
		sb.AppendLine("PROJECT MEMORY:");
		sb.AppendLine($"- Read the project memory file before making changes: {memoryFilePath}");
		sb.AppendLine("- This file stores durable context from previous runs so fresh sessions can avoid repeating mistakes.");
		sb.AppendLine("- Update this file whenever you discover stable project-specific guidance, workflow gotchas, or after you make and correct a mistake.");
		sb.AppendLine("- Keep entries factual, concise, and actionable for future agent runs.");
		sb.AppendLine("- Do not store secrets, credentials, tokens, or personal data in project memory.");
		sb.AppendLine($"- Keep the file under {ValidationLimits.ProjectMemoryMaxLength} characters.");
		sb.AppendLine("- VibeSwarm will sync changes from this file back into the project's stored memory after the job finishes.");

		if (string.IsNullOrWhiteSpace(project.Memory))
		{
			sb.AppendLine("- If the file is empty, create an initial memory entry once you learn something worth preserving.");
		}

		return sb.ToString().TrimEnd();
	}

	private static string BuildEnvironmentSection(Project project)
	{
		if (project.Environments == null || project.Environments.Count == 0)
		{
			return string.Empty;
		}

		var environments = project.Environments
			.Where(environment => environment.IsEnabled)
			.OrderByDescending(environment => environment.IsPrimary)
			.ThenBy(environment => environment.SortOrder)
			.ThenBy(environment => environment.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (environments.Count == 0)
		{
			return string.Empty;
		}

		var sb = new StringBuilder();
		sb.AppendLine("<environments>");
		sb.AppendLine("  Configured deployment targets for this project:");
		sb.AppendLine("  Prefer these URLs instead of assuming localhost.");
		sb.AppendLine("  Use the stage on each environment to decide what kinds of changes, deploys, and resets are appropriate.");
		foreach (var rule in BuildEnvironmentStageRules(environments))
		{
			sb.Append("  - ");
			sb.AppendLine(EscapeXml(rule));
		}
		if (environments.Any(environment => environment.Type == EnvironmentType.Web))
		{
			sb.AppendLine("  - Use Playwright MCP for browser interaction with web environments.");
		}

		var includedCount = 0;
		foreach (var environment in environments)
		{
			var lineBuilder = new StringBuilder();
			lineBuilder.Append("  - ");
			if (environment.IsPrimary)
			{
				lineBuilder.Append("Primary ");
			}
			lineBuilder.Append(environment.Type);
			lineBuilder.Append(" [");
			lineBuilder.Append(environment.Stage);
			lineBuilder.Append(']');
			lineBuilder.Append(": ");
			lineBuilder.Append(environment.Name);
			lineBuilder.Append(" | URL: ");
			lineBuilder.Append(environment.Url);

			if (!string.IsNullOrWhiteSpace(environment.Description))
			{
				lineBuilder.Append(" | Notes: ");
				lineBuilder.Append(environment.Description);
			}

			var hasUsername = !string.IsNullOrWhiteSpace(environment.Username);
			var hasPassword = !string.IsNullOrWhiteSpace(environment.Password);
			if (environment.Type == EnvironmentType.Web && (hasUsername || hasPassword))
			{
				lineBuilder.Append(" | Login: ");
				if (hasUsername)
				{
					lineBuilder.Append("Username=");
					lineBuilder.Append(environment.Username);
				}
				if (hasUsername && hasPassword)
				{
					lineBuilder.Append(", ");
				}
				if (hasPassword)
				{
					lineBuilder.Append("Password=");
					lineBuilder.Append(environment.Password);
				}
			}

			var escapedLine = EscapeXml(lineBuilder.ToString());
			if (sb.Length + escapedLine.Length + 32 > MaxEnvironmentSectionLength)
			{
				break;
			}

			sb.AppendLine(escapedLine);
			includedCount++;
		}

		var omittedCount = environments.Count - includedCount;
		if (omittedCount > 0)
		{
			sb.AppendLine($"  {omittedCount} additional environment(s) omitted for brevity.");
		}

		sb.AppendLine("</environments>");
		return sb.ToString();
	}

	private static string BuildTeamSection(Project project)
	{
		if (project.TeamAssignments == null || project.TeamAssignments.Count == 0)
		{
			return string.Empty;
		}

		var assignments = project.TeamAssignments
			.Where(assignment => assignment.IsEnabled && assignment.TeamRole != null)
			.OrderBy(assignment => assignment.TeamRole!.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (assignments.Count == 0)
		{
			return string.Empty;
		}

		var sb = new StringBuilder();
		sb.AppendLine("<team_roles>");
		sb.AppendLine("  Configured collaborator roles available for this repository:");
		foreach (var assignment in assignments)
		{
			var lineBuilder = new StringBuilder();
			lineBuilder.Append("  - ");
			lineBuilder.Append(assignment.TeamRole!.Name);

			if (!string.IsNullOrWhiteSpace(assignment.TeamRole.Description))
			{
				lineBuilder.Append(" | Summary: ");
				lineBuilder.Append(assignment.TeamRole.Description);
			}

			if (!string.IsNullOrWhiteSpace(assignment.TeamRole.Responsibilities))
			{
				lineBuilder.Append(" | Responsibilities: ");
				lineBuilder.Append(assignment.TeamRole.Responsibilities);
			}

			if (assignment.Provider != null)
			{
				lineBuilder.Append(" | Provider: ");
				lineBuilder.Append(assignment.Provider.Name);
			}

			if (!string.IsNullOrWhiteSpace(assignment.PreferredModelId))
			{
				lineBuilder.Append(" | Model: ");
				lineBuilder.Append(assignment.PreferredModelId);
			}

			var skills = assignment.TeamRole.SkillLinks
				.Where(link => link.Skill != null)
				.Select(link => link.Skill!.Name)
				.ToList();
			if (skills.Count > 0)
			{
				lineBuilder.Append(" | Skills: ");
				lineBuilder.Append(string.Join(", ", skills));
			}

			sb.AppendLine(EscapeXml(lineBuilder.ToString()));
		}

		sb.AppendLine("</team_roles>");
		return sb.ToString();
	}

	private static List<string> BuildEnvironmentStageRules(IEnumerable<ProjectEnvironment> environments)
	{
		var enabledStages = new HashSet<EnvironmentStage>(environments.Select(environment => environment.Stage));
		var rules = new List<string>();

		if (enabledStages.Contains(EnvironmentStage.Production))
		{
			rules.Add("Production environments are live/stable targets. Only make direct environment changes or redeploy them when the task explicitly asks for it, and expect slower deployments that may lag behind current code.");
		}

		if (enabledStages.Contains(EnvironmentStage.Development))
		{
			rules.Add("Development environments allow normal testing, iterative changes, and redeploys, but they may still need a deploy or restart before new code appears.");
		}

		if (enabledStages.Contains(EnvironmentStage.Local))
		{
			rules.Add("Local environments assume immediate feedback. It is acceptable to rebuild, restart services, redeploy, reseed data, or wipe the local database when the task benefits from it.");
		}

		return rules;
	}

	/// <summary>
	/// Builds a role-specific system prompt context block that is prepended to the agent's
	/// append-system-prompt when the job is part of a team swarm. This establishes the agent's
	/// persona, responsibilities, and coordination guidelines for parallel execution.
	/// </summary>
	public static string BuildRoleSystemPromptContext(TeamRole role, int totalSwarmSize)
	{
		var sb = new StringBuilder();

		sb.AppendLine($"You are acting as the {role.Name} for this project.");

		if (!string.IsNullOrWhiteSpace(role.Description))
		{
			sb.AppendLine($"Role summary: {role.Description.Trim()}");
		}

		if (!string.IsNullOrWhiteSpace(role.Responsibilities))
		{
			sb.AppendLine($"Your responsibilities: {role.Responsibilities.Trim()}");
		}

		if (totalSwarmSize > 1)
		{
			sb.AppendLine();
			sb.AppendLine($"You are one of {totalSwarmSize} specialized agents working in parallel on the same repository.");
			sb.AppendLine("Each agent focuses exclusively on their designated area of responsibility.");
			sb.AppendLine("Limit your changes to your area of expertise and avoid modifying files clearly owned by other roles.");
			sb.AppendLine("Make small, focused, atomic commits so that parallel work integrates cleanly.");
		}

		return sb.ToString().TrimEnd();
	}

	private static string EscapeXml(string value)
	{
		return value
			.Replace("&", "&amp;")
			.Replace("<", "&lt;")
			.Replace(">", "&gt;")
			.Replace("\"", "&quot;")
			.Replace("'", "&apos;");
	}
}
