using System.Text;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public static class ProviderPlanningHelper
{
	/// <summary>
	/// Tools that must not run during the planning stage.
	/// Planning is a read-only exploration phase — the agent must not edit files,
	/// run shell commands, or make git commits. Those actions belong to the
	/// execution stage that follows.
	/// </summary>
	public static readonly List<string> PlanningDisallowedTools =
	[
		"Bash",
		"Edit",
		"Write",
		"MultiEdit",
		"NotebookEdit",
		"TodoWrite"
	];

	public static bool SupportsPlanningMode(ProviderType providerType)
	{
		return providerType is ProviderType.Claude or ProviderType.Copilot;
	}

	public static string BuildPlanningPrompt(ProviderType providerType, string requestDescription)
	{
		var description = string.IsNullOrWhiteSpace(requestDescription)
			? "No task description was provided."
			: requestDescription.Trim();

		var sb = new StringBuilder();
		sb.AppendLine("Explore the codebase and create an implementation-ready plan for the request below.");
		sb.AppendLine("This is a read-only planning phase. Do not edit files, run shell commands, or make commits.");
		sb.AppendLine("A separate execution agent will implement the plan once it is complete.");
		sb.AppendLine();
		sb.AppendLine("## Request");
		sb.AppendLine(description);
		sb.AppendLine();
		sb.AppendLine("## Planning Requirements");
		sb.AppendLine("1. Overview: Summarize the intended outcome");
		sb.AppendLine("2. User Experience: Describe the expected user-visible behavior");
		sb.AppendLine("3. Components: Identify the files, services, models, and UI surfaces involved");
		sb.AppendLine("4. Implementation Steps: Outline a practical build order");
		sb.AppendLine("5. Edge Cases: Call out important validation, failure, and empty-state scenarios");
		sb.AppendLine("6. Verification: List the build and test checks that should confirm completion");
		sb.AppendLine();
		sb.AppendLine("Return only the plan/specification. Do not implement the feature and do not include code samples.");
		return sb.ToString().TrimEnd();
	}

	public static string? ExtractExecutionText(ExecutionResult result)
	{
		var preferredRoles = new[] { "plan", "assistant", "response", "message", "suggestion" };
		foreach (var role in preferredRoles)
		{
			var content = result.Messages
				.Where(message =>
					string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase) &&
					!string.IsNullOrWhiteSpace(message.Content))
				.Select(message => message.Content.Trim())
				.ToList();
			if (content.Count > 0)
			{
				return string.Join(Environment.NewLine + Environment.NewLine, content);
			}
		}

		return string.IsNullOrWhiteSpace(result.Output)
			? null
			: result.Output.Trim();
	}
}
