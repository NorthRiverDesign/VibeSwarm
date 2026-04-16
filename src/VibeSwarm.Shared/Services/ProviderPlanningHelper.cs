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

	public static string BuildPlanningPrompt(ProviderType providerType, string requestDescription, string? templateOverride = null)
	{
		var description = string.IsNullOrWhiteSpace(requestDescription)
			? "No task description was provided."
			: requestDescription.Trim();

		var template = string.IsNullOrWhiteSpace(templateOverride)
			? PromptBuilder.DefaultPlanningPromptTemplate
			: templateOverride;

		return template.Replace(PromptBuilder.RequestToken, description, StringComparison.Ordinal).TrimEnd();
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
