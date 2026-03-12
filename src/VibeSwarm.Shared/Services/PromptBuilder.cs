using System.Text;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Builds structured XML-tagged prompts for CLI agents to improve task clarity and reduce wasted tokens.
/// Wraps raw user prompts with project context, constraints, and structured formatting.
/// </summary>
public static class PromptBuilder
{
	private const int MaxPromptLength = 2000;
	private const int XmlOverhead = 200;
	private const int MaxEnvironmentSectionLength = 700;

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

		var hasConstraints = !string.IsNullOrWhiteSpace(job.Project.PromptContext)
			|| job.MaxCostUsd.HasValue
			|| !string.IsNullOrWhiteSpace(job.Branch);

		if (hasConstraints)
		{
			sb.AppendLine("<constraints>");

			if (!string.IsNullOrWhiteSpace(job.Project.PromptContext))
			{
				var context = job.Project.PromptContext.Trim();
				var availableSpace = MaxPromptLength - XmlOverhead - job.GoalPrompt.Length - environmentSection.Length - 100;
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
				sb.AppendLine($"  Target branch: {job.Branch}");
			}

			sb.AppendLine("  Only modify files directly related to the task.");
			sb.AppendLine("  Do not refactor unrelated code.");
			sb.AppendLine("</constraints>");
		}

		var result = sb.ToString().TrimEnd();
		return result.Length > MaxPromptLength ? job.GoalPrompt : result;
	}

	public static string? BuildSystemPromptRules(Project? project, bool injectEfficiencyRules = true, bool injectRepoMap = true)
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
			sb.AppendLine("- Verify your changes compile/build before finishing.");
			sb.AppendLine("- If you encounter issues unrelated to the task, note them but do not fix them.");
		}

		if (project.Environments.Any(environment => environment.IsEnabled && environment.Type == EnvironmentType.Web))
		{
			if (sb.Length > 0)
			{
				sb.AppendLine();
			}

			sb.AppendLine("DEPLOYED ENVIRONMENTS:");
			sb.AppendLine("- Use Playwright MCP when browser interaction is needed against configured web environments.");
			sb.AppendLine("- Do not assume localhost when a project environment URL is available.");
			sb.AppendLine("- Deployed environments may lag behind current repository changes until a deployment occurs.");
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
		sb.AppendLine("  Deployed web environments may not reflect the latest local code until a deployment has happened.");
		sb.AppendLine("  Use Playwright MCP for browser interaction with web environments.");

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
			lineBuilder.Append(": ");
			lineBuilder.Append(environment.Name);
			lineBuilder.Append(" | URL: ");
			lineBuilder.Append(environment.Url);

			if (!string.IsNullOrWhiteSpace(environment.Description))
			{
				lineBuilder.Append(" | Notes: ");
				lineBuilder.Append(environment.Description);
			}

			if (environment.Type == EnvironmentType.Web
				&& !string.IsNullOrWhiteSpace(environment.Username)
				&& !string.IsNullOrWhiteSpace(environment.Password))
			{
				lineBuilder.Append(" | Login: ");
				lineBuilder.Append(environment.Username);
				lineBuilder.Append(" / ");
				lineBuilder.Append(environment.Password);
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
