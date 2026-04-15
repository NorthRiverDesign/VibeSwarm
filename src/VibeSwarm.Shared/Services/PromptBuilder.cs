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
	private const int MaxSkillSummaryLength = 160;
	public const string IdeaToken = "{{idea}}";
	public const string SpecificationToken = "{{specification}}";

	public static string DefaultIdeaExpansionPromptTemplate =>
		"""
		You are a staff-level software engineer turning a product idea into an implementation-ready specification.

		## Feature Idea
		{{idea}}

		## Instructions
		1. Inspect the codebase, related flows, reusable components, and tests first. Use subagents when they help.
		2. Fill in missing details from repository patterns and choose the option that best fits the current system.
		3. Return concise markdown with: Overview, User Flows, Affected Areas, Implementation Plan, Edge Cases, Acceptance Criteria.
		4. Keep it concrete. No code samples or provider/model attribution.
		""";

	public static string DefaultIdeaImplementationPromptTemplate =>
		"""
		You are a staff-level software engineer implementing a feature directly from a product idea.

		## Feature Idea
		{{idea}}

		## Instructions
		1. Inspect the codebase, related flows, reusable components, and tests before editing. Use subagents when they help.
		2. Fill in missing details from repository patterns and prefer the simplest solution that fully satisfies the idea.
		3. Reuse existing patterns, helpers, and components before adding new ones.
		4. Implement the feature end-to-end with the needed UX, validation, persistence, error handling, and tests.
		5. Keep changes scoped, preserve existing behavior unless the idea requires a change, and leave the repository in a working state.
		6. Do not mention or attribute the work to any provider, model, or CLI tool.

		Implement this feature now without first writing a separate specification.

		When you are finished, end your response with a short summary in this exact format:
		<commit-summary>
		A concise one-line description of what was implemented (max 72 chars)
		</commit-summary>
		""";

	public static string DefaultApprovedIdeaImplementationPromptTemplate =>
		"""
		You are a staff-level software engineer implementing an approved specification.

		## Original Idea
		{{idea}}

		## Detailed Specification
		{{specification}}

		## Instructions
		1. Use the approved specification as the source of truth, then fill in missing details from repository patterns.
		2. Inspect the codebase, related flows, reusable components, and tests before editing. Use subagents when they help.
		3. Reuse existing patterns, helpers, and components before adding new ones.
		4. Implement the feature end-to-end with the needed UX, validation, persistence, error handling, and tests.
		5. Keep changes scoped, preserve existing behavior unless the specification requires a change, and leave the repository in a working state.
		6. Do not mention or attribute the work to any provider, model, or CLI tool.

		Implement this feature now.

		When you are finished, end your response with a short summary in this exact format:
		<commit-summary>
		A concise one-line description of what was implemented (max 72 chars)
		</commit-summary>
		""";

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
		var skillSection = BuildSkillSection(job.Project);
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

		if (!string.IsNullOrWhiteSpace(skillSection))
		{
			sb.Append(skillSection);
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
				var availableSpace = MaxPromptLength - XmlOverhead - job.GoalPrompt.Length - environmentSection.Length - teamSection.Length - skillSection.Length - 100;
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
		sb.AppendLine("Treat the implementation plan above as approved.");
		sb.AppendLine("Implement it now. Only revisit planning if execution reveals missing information.");
		return sb.ToString().TrimEnd();
	}

	public static string BuildIdeaExpansionPrompt(string ideaDescription, string? template = null)
	{
		return ApplyIdeaPromptTemplate(
			string.IsNullOrWhiteSpace(template) ? DefaultIdeaExpansionPromptTemplate : template,
			[
				new TemplateToken(IdeaToken, ideaDescription, "## Feature Idea")
			]);
	}

	public static string BuildIdeaImplementationPrompt(string ideaDescription, string? template = null)
	{
		return ApplyIdeaPromptTemplate(
			string.IsNullOrWhiteSpace(template) ? DefaultIdeaImplementationPromptTemplate : template,
			[
				new TemplateToken(IdeaToken, ideaDescription, "## Feature Idea")
			]);
	}

	public static string BuildApprovedIdeaImplementationPrompt(string originalIdea, string expandedDescription, string? template = null)
	{
		return ApplyIdeaPromptTemplate(
			string.IsNullOrWhiteSpace(template) ? DefaultApprovedIdeaImplementationPromptTemplate : template,
			[
				new TemplateToken(IdeaToken, originalIdea, "## Original Idea"),
				new TemplateToken(SpecificationToken, expandedDescription, "## Detailed Specification")
			]);
	}

	public static string? BuildIdeaSystemPromptRules(
		Project? project,
		bool injectEfficiencyRules = true,
		bool injectRepoMap = true)
	{
		return BuildSystemPromptRules(project, injectEfficiencyRules, injectRepoMap, providerType: null);
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
			sb.AppendLine("- Do only the requested work. Do not modify unrelated files.");
			sb.AppendLine("- Do not add comments, docstrings, or type annotations to untouched code.");
			sb.AppendLine("- Do not refactor beyond the request.");
			sb.AppendLine("- If you spot unrelated issues, note them without fixing them.");
			sb.AppendLine();
			sb.AppendLine("BUILD VERIFICATION (CRITICAL):");
			sb.AppendLine("- Verify the project builds before finishing.");

			if (!string.IsNullOrWhiteSpace(project.BuildCommand))
			{
				sb.AppendLine($"- Run the project build command: {project.BuildCommand.Trim()}");
			}
			else
			{
				sb.AppendLine("- Run the appropriate build command for this project (for example: dotnet build, npm run build, cargo build).");
			}

			if (!string.IsNullOrWhiteSpace(project.TestCommand))
			{
				sb.AppendLine($"- Run the project test command: {project.TestCommand.Trim()}");
			}

			sb.AppendLine("- If the build or tests fail, fix them before finishing.");
			sb.AppendLine("- Do not leave the repository in a broken state.");

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

		if (enabledEnvironments.Any(environment =>
			environment.Type == EnvironmentType.Web &&
			(!string.IsNullOrWhiteSpace(environment.Username) || !string.IsNullOrWhiteSpace(environment.Password))))
		{
			if (sb.Length > 0)
			{
				sb.AppendLine();
			}

			sb.AppendLine("ENVIRONMENT AUTHENTICATION:");
			sb.AppendLine("- When a configured web environment includes login credentials, use those exact values for browser automation.");
			sb.AppendLine("- Do not invent placeholder or guessed accounts such as test@test.com when environment credentials are available.");
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
		if (project.AgentAssignments == null || project.AgentAssignments.Count == 0)
		{
			return string.Empty;
		}

		var assignments = project.AgentAssignments
			.Where(assignment => assignment.IsEnabled && assignment.Agent != null)
			.OrderBy(assignment => assignment.Agent!.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (assignments.Count == 0)
		{
			return string.Empty;
		}

		var sb = new StringBuilder();
		sb.AppendLine("<agents>");
		sb.AppendLine("  Configured agent presets available for this repository:");
		foreach (var assignment in assignments)
		{
			var lineBuilder = new StringBuilder();
			lineBuilder.Append("  - ");
			lineBuilder.Append(assignment.Agent!.Name);

			if (!string.IsNullOrWhiteSpace(assignment.Agent.Description))
			{
				lineBuilder.Append(" | Purpose: ");
				lineBuilder.Append(assignment.Agent.Description);
			}

			if (!string.IsNullOrWhiteSpace(assignment.Agent.Responsibilities))
			{
				lineBuilder.Append(" | Instructions: ");
				lineBuilder.Append(assignment.Agent.Responsibilities);
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

			var skills = assignment.Agent.SkillLinks
				.Where(link => link.Skill != null)
				.Select(link => link.Skill!.Name)
				.ToList();
			if (skills.Count > 0)
			{
				lineBuilder.Append(" | Skills: ");
				lineBuilder.Append(string.Join(", ", skills));
			}

			var cycleDefaults = DescribeAgentCycleDefaults(assignment.Agent);
			if (!string.IsNullOrWhiteSpace(cycleDefaults))
			{
				lineBuilder.Append(" | Execution: ");
				lineBuilder.Append(cycleDefaults);
			}

			sb.AppendLine(EscapeXml(lineBuilder.ToString()));
		}

		sb.AppendLine("</agents>");
		return sb.ToString();
	}

	private static string BuildSkillSection(Project project)
	{
		var skills = ProjectSkillHelper.GetConfiguredSkills(project);
		if (skills.Count == 0)
		{
			return string.Empty;
		}

		var sb = new StringBuilder();
		sb.AppendLine("<available_skills>");
		sb.AppendLine("  App-configured MCP skills available to compatible providers for this job. Use them when relevant instead of guessing project conventions:");
		foreach (var skill in skills)
		{
			var lineBuilder = new StringBuilder();
			lineBuilder.Append("  - ");
			lineBuilder.Append(skill.Name);

			var summary = BuildSkillSummary(skill);
			if (!string.IsNullOrWhiteSpace(summary))
			{
				lineBuilder.Append(" | Use for: ");
				lineBuilder.Append(summary);
			}

			sb.AppendLine(EscapeXml(lineBuilder.ToString()));
		}

		sb.AppendLine("</available_skills>");
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
	public static string BuildRoleSystemPromptContext(Agent role, int totalSwarmSize)
	{
		var sb = new StringBuilder();

		sb.AppendLine($"You are acting as the {role.Name} agent for this project.");

		if (!string.IsNullOrWhiteSpace(role.Description))
		{
			sb.AppendLine($"Agent purpose: {role.Description.Trim()}");
		}

		if (!string.IsNullOrWhiteSpace(role.Responsibilities))
		{
			sb.AppendLine($"Your instructions: {role.Responsibilities.Trim()}");
		}

		var skills = role.SkillLinks
			.Where(link => link.Skill != null && link.Skill.IsEnabled)
			.Select(link => link.Skill!)
			.GroupBy(skill => skill.Id)
			.Select(group => group.First())
			.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (skills.Count > 0)
		{
			sb.AppendLine($"Available MCP skills for this agent: {string.Join("; ", skills.Select(FormatSkillReference))}");
			sb.AppendLine("Use those skills when they match the task instead of guessing project-specific conventions.");
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

	private static string FormatSkillReference(Skill skill)
	{
		var summary = BuildSkillSummary(skill);
		return string.IsNullOrWhiteSpace(summary)
			? skill.Name
			: $"{skill.Name} - {summary}";
	}

	private static string? DescribeAgentCycleDefaults(Agent role)
	{
		if (role.DefaultCycleMode == CycleMode.SingleCycle)
		{
			return null;
		}

		var cycleMode = role.DefaultCycleMode switch
		{
			CycleMode.FixedCount => $"fixed-count ({role.DefaultMaxCycles} cycles)",
			CycleMode.Autonomous => $"autonomous (max {role.DefaultMaxCycles} cycles)",
			_ => null
		};
		if (string.IsNullOrWhiteSpace(cycleMode))
		{
			return null;
		}

		var sessionMode = role.DefaultCycleSessionMode == CycleSessionMode.ContinueSession
			? "resume session"
			: "fresh session";
		return $"{cycleMode}, {sessionMode}";
	}

	private static string? BuildSkillSummary(Skill skill)
	{
		if (string.IsNullOrWhiteSpace(skill.Description))
		{
			return null;
		}

		var summary = string.Join(" ", skill.Description.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
		if (summary.Length <= MaxSkillSummaryLength)
		{
			return summary;
		}

		return $"{summary[..(MaxSkillSummaryLength - 3)].TrimEnd()}...";
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

	private static string ApplyIdeaPromptTemplate(string template, IReadOnlyList<TemplateToken> tokens)
	{
		var missingTokens = new List<TemplateToken>();
		var prompt = template.Trim().ReplaceLineEndings("\n");

		foreach (var token in tokens)
		{
			var containsToken = prompt.Contains(token.Placeholder, StringComparison.Ordinal);
			prompt = prompt.Replace(token.Placeholder, token.Value.Trim(), StringComparison.Ordinal);
			if (!containsToken)
			{
				missingTokens.Add(token);
			}
		}

		if (missingTokens.Count == 0)
		{
			return prompt.TrimEnd();
		}

		var sb = new StringBuilder(prompt.TrimEnd());
		foreach (var token in missingTokens)
		{
			sb.AppendLine();
			sb.AppendLine();
			sb.AppendLine(token.FallbackHeading);
			sb.AppendLine(token.Value.Trim());
		}

		return sb.ToString().TrimEnd();
	}

	private sealed record TemplateToken(string Placeholder, string Value, string FallbackHeading);
}
