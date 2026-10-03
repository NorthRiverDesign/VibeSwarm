using System.Text;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Builds structured XML-tagged prompts for CLI agents to improve task clarity and reduce wasted tokens.
/// Wraps raw user prompts with project context, constraints, and structured formatting.
/// </summary>
public static class PromptBuilder
{
	private const int MaxEnvironmentEntriesLength = 1200;
	private const int MaxSkillSummaryLength = 160;
	public const string IdeaToken = "{{idea}}";
	public const string SpecificationToken = "{{specification}}";
	public const string RequestToken = "{{request}}";

	/// <summary>
	/// Default template for the read-only planning stage. Substitutes <see cref="RequestToken"/>.
	/// Consumed by <see cref="ProviderPlanningHelper.BuildPlanningPrompt"/> so planning-stage agents
	/// share the same templating mechanism as idea expansion and implementation.
	/// </summary>
	public static string DefaultPlanningPromptTemplate =>
		"""
		Explore the codebase and write an implementation-ready plan for the request below.
		Read-only planning only. Do not edit files, run shell commands, or make commits.
		A separate execution agent will implement the approved plan.

		## Request
		{{request}}

		## Plan
		1. Outcome
		2. User Experience
		3. Affected Areas
		4. Implementation Steps
		5. Edge Cases
		6. Verification

		Return only the plan. Do not implement the feature or include code samples.
		""";

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
		A concise one-line description of what was implemented (aim for 72 chars; hard max 96 chars)
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
		A concise one-line description of what was implemented (aim for 72 chars; hard max 96 chars)
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

			// Sent in full, like the goal: both are length-limited where they are saved, and
			// a goal built from an idea runs well past any fixed total for the whole prompt.
			if (!string.IsNullOrWhiteSpace(job.Project.PromptContext))
			{
				sb.AppendLine($"  {job.Project.PromptContext.Trim()}");
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
				sb.AppendLine("  VibeSwarm opens a pull request for your changes after you finish. Do not open one yourself.");
			}

			sb.AppendLine("  Only modify files directly related to the task.");
			sb.AppendLine("  Do not refactor unrelated code.");
			sb.AppendLine("</constraints>");
		}

		return sb.ToString().TrimEnd();
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

	public static string BuildRecoveryPrompt(
		string basePrompt,
		string? recoveryPrompt,
		string? recentConsoleOutput,
		string? lastResumeFailureReason,
		bool forceFreshSession)
	{
		var effectivePrompt = string.IsNullOrWhiteSpace(recoveryPrompt)
			? basePrompt.Trim()
			: recoveryPrompt.Trim();

		var sb = new StringBuilder();
		sb.AppendLine(effectivePrompt);
		sb.AppendLine();
		sb.AppendLine("<recovery_context>");
		sb.AppendLine("A previous execution of this exact job was interrupted before completion.");
		sb.AppendLine(forceFreshSession
			? "The previous provider session could not be resumed. Start a fresh session, inspect the recovery context below, and continue from the current repository state without redoing completed work."
			: "Resume from the existing provider session if it is still available. Before making changes, inspect the current repository state and continue from where the interrupted run left off.");

		if (!string.IsNullOrWhiteSpace(lastResumeFailureReason))
		{
			sb.AppendLine($"Last resume failure: {lastResumeFailureReason.Trim()}");
		}

		if (!string.IsNullOrWhiteSpace(recentConsoleOutput))
		{
			sb.AppendLine("<recent_console_output>");
			sb.AppendLine(recentConsoleOutput.Trim());
			sb.AppendLine("</recent_console_output>");
		}

		sb.AppendLine("</recovery_context>");
		return sb.ToString().TrimEnd();
	}

	public static string BuildInteractionResumePrompt(
		string basePrompt,
		string interactionPrompt,
		string userResponse,
		string? recentConsoleOutput)
	{
		var sb = new StringBuilder();
		sb.AppendLine(basePrompt.Trim());
		sb.AppendLine();
		sb.AppendLine("<interaction_context>");
		sb.AppendLine("A previous execution paused because the provider output appeared to request user input.");
		sb.AppendLine("That execution was stopped before continuing so no additional automated changes were made after the pause.");
		sb.AppendLine();
		sb.AppendLine("<provider_prompt>");
		sb.AppendLine(interactionPrompt.Trim());
		sb.AppendLine("</provider_prompt>");
		sb.AppendLine();
		sb.AppendLine("<user_response>");
		sb.AppendLine(userResponse.Trim());
		sb.AppendLine("</user_response>");

		if (!string.IsNullOrWhiteSpace(recentConsoleOutput))
		{
			sb.AppendLine();
			sb.AppendLine("<recent_console_output>");
			sb.AppendLine(recentConsoleOutput.Trim());
			sb.AppendLine("</recent_console_output>");
		}

		sb.AppendLine();
		sb.AppendLine("Treat the user response above as authoritative additional guidance.");
		sb.AppendLine("If the earlier provider output was only informational and not a real question, continue the job normally from the current repository state.");
		sb.AppendLine("</interaction_context>");
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

	public static string? BuildSystemPromptRules(
		Project? project,
		bool injectEfficiencyRules = true,
		bool injectRepoMap = true,
		bool requireCodeChange = true)
	{
		if (project == null)
		{
			return null;
		}

		var sb = new StringBuilder();

		if (injectEfficiencyRules)
		{
			sb.AppendLine("IMPORTANT RULES:");
			sb.AppendLine("- Do only the requested work. Do not modify unrelated files, refactor beyond the request, or add comments, docstrings, or type annotations to untouched code. Note unrelated issues instead of fixing them.");
			sb.AppendLine();
			sb.AppendLine("BUILD VERIFICATION (CRITICAL):");

			var buildStep = string.IsNullOrWhiteSpace(project.BuildCommand)
				? "the appropriate build command for this project (for example: dotnet build, npm run build, cargo build)"
				: $"`{project.BuildCommand.Trim()}`";
			sb.AppendLine(string.IsNullOrWhiteSpace(project.TestCommand)
				? $"- Verify the project builds before finishing. Run {buildStep} and fix any failures."
				: $"- Verify the project builds before finishing. Run {buildStep}, then `{project.TestCommand.Trim()}`, and fix any failures.");
			sb.AppendLine("- Do not leave the repository in a broken state. The next queued job starts from it.");
			// Installing dependencies rewrites lockfiles when the local tool version differs
			// from the one that wrote them, and everything in the tree gets committed. That
			// churn lands in every commit and reverses itself on the next machine.
			sb.AppendLine("- Install dependencies without rewriting lockfiles (npm ci, not npm install; composer install, not update). Unless the task changes dependencies, restore any lockfile a build rewrote.");
			sb.AppendLine();
			// Jobs run one after another from a queue, unattended. VibeSwarm owns git: it
			// resets the checkout before each job and commits the working tree after it,
			// with its own attribution settings, so agent commits only get in the way.
			sb.AppendLine("COMPLETING THE JOB:");
			sb.AppendLine("- This job runs unattended in a queue. Do not stop to ask questions or wait for confirmation; a question pauses the queue. Make the reasonable call and keep going.");
			if (requireCodeChange)
			{
				sb.AppendLine("- The deliverable is a code change. A run that leaves the working tree unchanged is recorded as failed.");
			}
			sb.AppendLine("- Leave git to VibeSwarm unless the task says otherwise: do not commit, push, stash, reset, rebase, or switch branches. It delivers your working-tree changes after you exit.");
			sb.AppendLine("- End with a short summary of what changed, how you verified it, assumptions, and anything left undone. Make its last line the commit subject: <commit-summary>A concise one-line description of what was implemented (aim for 72 chars; hard max 96 chars)</commit-summary>");
			sb.AppendLine();
			sb.AppendLine("SESSION ARTIFACTS:");
			sb.AppendLine("- Anything left in the working tree may be committed. Write screenshots, browser traces, test logs and reports, scratch scripts and temp files under /tmp or the git-ignored .vibeswarm/ folder.");
			sb.AppendLine("- If a tool can only write inside the repository, delete its output before finishing or list the path in .git/info/exclude, not .gitignore.");
			sb.AppendLine("- Do not add agent instruction, plan or memory files (such as CLAUDE.md, AGENTS.md or notes) unless the task asks for them.");
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
			sb.AppendLine("- Use Playwright MCP for browser work against configured web environments. Do not assume localhost when a project environment URL is available; deployed code may lag behind the repository until a deploy or restart.");
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
			sb.AppendLine("- When a web environment has login credentials, use those exact values for browser automation, never guessed accounts such as test@test.com.");
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

	/// <summary>
	/// Rules for a "Set up local environment" job: get the project running on this machine and
	/// report where it runs in <paramref name="resultFilePath"/>, which VibeSwarm turns into the
	/// project's Local environment.
	/// </summary>
	public static string BuildLocalEnvironmentSetupRules(string resultFilePath)
	{
		var sb = new StringBuilder();
		sb.AppendLine("LOCAL ENVIRONMENT SETUP:");
		sb.AppendLine("- This job prepares the project to run on this machine. Success is an app that starts and responds here, not a code change, so it may finish without changing tracked files.");
		sb.AppendLine("- Learn what the project needs from its README and docs, sample config (.env.example and similar), docker-compose files, package manifests and CI config.");
		sb.AppendLine("- Prefer tools and services already installed or running on this machine. When something needs root or is unavailable, use a self-contained alternative (SQLite, a container if Docker is available, a log or file mailer) and say what is missing.");
		sb.AppendLine("- Database: create a dedicated local database and user for this project. Never point at a production or shared database. Run migrations and seed development data when the project provides them.");
		sb.AppendLine("- Email: send outgoing mail to a local trap (Mailpit, MailHog, or the framework's log or file mailer) so nothing reaches real inboxes.");
		sb.AppendLine("- Configuration: create missing local config files from their examples with local values. Generate fresh local secrets, never copy production credentials, and use test or stub keys for third-party services.");
		sb.AppendLine("- Keep machine-specific files out of git: check that every file you create (.env, local databases, uploads) is ignored, and add any that are not to .git/info/exclude, not .gitignore.");
		sb.AppendLine("- Change tracked files only when the project cannot run locally without it, and keep those changes free of machine-specific values.");
		sb.AppendLine("- Use ports that are free on this machine. Start the app, confirm it responds (curl or a browser), then stop what you started, except background services such as the database or mail trap.");
		sb.AppendLine($"- Before finishing, write {resultFilePath} as JSON: {{\"url\": \"http://localhost:<port>\", \"startCommand\": \"<command that starts the app>\", \"notes\": \"<services, ports, mail trap URL, how to reset data>\", \"username\": \"<local login, if the app has one>\", \"password\": \"<its password>\"}}. Leave out what does not apply.");
		sb.AppendLine("- VibeSwarm saves that file as the project's Local environment so later jobs can start and test the app. Do not put the values anywhere else in the repository.");
		sb.AppendLine("- End with how to start the app, its URL, and the services you set up.");
		return sb.ToString().TrimEnd();
	}

	public static string? BuildProjectMemoryRules(Project? project, string? memoryFilePath)
	{
		if (project == null || string.IsNullOrWhiteSpace(memoryFilePath))
		{
			return null;
		}

		var sb = new StringBuilder();
		sb.AppendLine("PROJECT MEMORY:");
		sb.AppendLine($"- Read {memoryFilePath} before making changes. It holds durable context from earlier runs.");
		sb.AppendLine($"- Update this file when you learn stable project guidance or workflow gotchas, or after you make and correct a mistake. Keep entries factual, concise and actionable, the file under {ValidationLimits.ProjectMemoryMaxLength} characters, and secrets, credentials and personal data out.");
		sb.AppendLine(string.IsNullOrWhiteSpace(project.Memory)
			? "- If the file is empty, add a first entry once you learn something worth keeping. VibeSwarm will sync changes back to the project after the job."
			: "- VibeSwarm will sync changes back to the project after the job.");

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
		sb.AppendLine("  Configured deployment targets. Prefer these URLs instead of assuming localhost. Let each stage decide which changes, deploys, and resets are appropriate.");
		foreach (var rule in BuildEnvironmentStageRules(environments))
		{
			sb.Append("  - ");
			sb.AppendLine(EscapeXml(rule));
		}
		if (environments.Any(environment => environment.Type == EnvironmentType.Web))
		{
			sb.AppendLine("  - Use Playwright MCP for browser interaction with web environments.");
		}

		// The budget covers the environment entries only, so the fixed guidance above never
		// crowds out the environments themselves.
		var includedCount = 0;
		var entriesLength = 0;
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
			if (entriesLength + escapedLine.Length > MaxEnvironmentEntriesLength)
			{
				break;
			}

			sb.AppendLine(escapedLine);
			entriesLength += escapedLine.Length;
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
		sb.AppendLine("  Skills are folders of instructions installed on this machine. When a skill");
		sb.AppendLine("  matches the task, read its SKILL.md (and any referenced files in the skill's");
		sb.AppendLine("  folder) before acting. Honor each skill's allowed-tools list when present:");
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

			if (!string.IsNullOrWhiteSpace(skill.StoragePath))
			{
				lineBuilder.Append(" | SKILL.md: ");
				lineBuilder.Append(Path.Combine(skill.StoragePath, "SKILL.md"));
			}

			if (!string.IsNullOrWhiteSpace(skill.AllowedTools))
			{
				lineBuilder.Append(" | allowed-tools: ");
				lineBuilder.Append(skill.AllowedTools.Trim());
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
	/// persona, responsibilities, and coordination guidelines for taking turns on one checkout.
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
			sb.AppendLine($"Skills installed for this agent: {string.Join("; ", skills.Select(FormatSkillReference))}");
			sb.AppendLine("Read each skill's SKILL.md from the path shown before acting on it, and honor any allowed-tools restrictions declared there.");
		}

		if (totalSwarmSize > 1)
		{
			sb.AppendLine();
			sb.AppendLine($"You are one of {totalSwarmSize} specialized agents taking turns on the same repository; the others run before or after you, never at the same time.");
			sb.AppendLine("Each agent focuses exclusively on their designated area of responsibility.");
			sb.AppendLine("Limit your changes to your area of expertise and avoid modifying files clearly owned by other roles.");
			sb.AppendLine("Keep your changes small and focused, and build on what earlier agents left in the repository rather than redoing or reverting it.");
		}

		return sb.ToString().TrimEnd();
	}

	private static string FormatSkillReference(Skill skill)
	{
		var parts = new List<string> { skill.Name };

		var summary = BuildSkillSummary(skill);
		if (!string.IsNullOrWhiteSpace(summary))
		{
			parts.Add(summary);
		}

		if (!string.IsNullOrWhiteSpace(skill.StoragePath))
		{
			parts.Add($"SKILL.md: {Path.Combine(skill.StoragePath, "SKILL.md")}");
		}

		if (!string.IsNullOrWhiteSpace(skill.AllowedTools))
		{
			parts.Add($"allowed-tools: {skill.AllowedTools.Trim()}");
		}

		return string.Join(" - ", parts);
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
