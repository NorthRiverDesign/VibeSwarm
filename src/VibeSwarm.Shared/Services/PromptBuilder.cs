using System.Text;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Builds structured XML-tagged prompts for CLI agents to improve task clarity and reduce wasted tokens.
/// Wraps raw user prompts with project context, constraints, and structured formatting.
/// </summary>
public static class PromptBuilder
{
	/// <summary>
	/// Maximum total length for the structured prompt to stay within the GoalPrompt limit.
	/// </summary>
	private const int MaxPromptLength = 2000;

	/// <summary>
	/// Approximate overhead added by XML wrapping tags.
	/// </summary>
	private const int XmlOverhead = 200;

	/// <summary>
	/// Builds a structured XML-tagged prompt from a job's goal prompt and project context.
	/// If the job has no project or structuring is not applicable, returns the raw GoalPrompt.
	/// </summary>
	/// <param name="job">The job containing the goal prompt and project reference.</param>
	/// <param name="enableStructuring">Whether prompt structuring is enabled in settings.</param>
	/// <returns>A structured prompt string with XML tags, or the raw GoalPrompt if structuring is disabled.</returns>
	public static string BuildStructuredPrompt(Job job, bool enableStructuring = true)
	{
		if (job == null)
			return string.Empty;

		if (!enableStructuring || job.Project == null)
			return job.GoalPrompt;

		var sb = new StringBuilder();

		// Task section - the user's goal
		sb.AppendLine("<task>");
		sb.AppendLine(job.GoalPrompt.Trim());
		sb.AppendLine("</task>");

		// Project section
		sb.AppendLine("<project>");
		sb.AppendLine($"  <name>{EscapeXml(job.Project.Name)}</name>");
		if (!string.IsNullOrWhiteSpace(job.Project.Description))
		{
			sb.AppendLine($"  <description>{EscapeXml(job.Project.Description)}</description>");
		}
		sb.AppendLine("</project>");

		// Constraints section
		var hasConstraints = !string.IsNullOrWhiteSpace(job.Project.PromptContext)
			|| job.MaxCostUsd.HasValue
			|| !string.IsNullOrWhiteSpace(job.Branch);

		if (hasConstraints)
		{
			sb.AppendLine("<constraints>");

			if (!string.IsNullOrWhiteSpace(job.Project.PromptContext))
			{
				var context = job.Project.PromptContext.Trim();
				// Truncate project context if needed to stay within limits
				var availableSpace = MaxPromptLength - XmlOverhead - job.GoalPrompt.Length - 100;
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

		// If the structured prompt exceeds the limit, fall back to raw prompt
		if (result.Length > MaxPromptLength)
			return job.GoalPrompt;

		return result;
	}

	/// <summary>
	/// Builds the system prompt rules to append to the provider's default system prompt.
	/// Includes efficiency rules and optional repo map.
	/// </summary>
	/// <param name="project">The project associated with the job.</param>
	/// <param name="injectEfficiencyRules">Whether to inject efficiency rules.</param>
	/// <param name="injectRepoMap">Whether to inject the repo map.</param>
	/// <returns>System prompt rules string, or null if nothing to inject.</returns>
	public static string? BuildSystemPromptRules(Project? project, bool injectEfficiencyRules = true, bool injectRepoMap = true)
	{
		if (project == null)
			return null;

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

		if (injectRepoMap && !string.IsNullOrWhiteSpace(project.RepoMap))
		{
			if (sb.Length > 0)
				sb.AppendLine();

			sb.AppendLine("PROJECT STRUCTURE:");
			sb.AppendLine(project.RepoMap.Trim());
		}

		return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
	}

	/// <summary>
	/// Escapes special XML characters in a string.
	/// </summary>
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
