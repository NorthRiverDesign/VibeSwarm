using System.Text;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

public static class ProviderPlanningHelper
{
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
		if (providerType == ProviderType.Claude)
		{
			sb.AppendLine("/plan Create an implementation-ready plan for the request below before any coding begins.");
		}
		else
		{
			sb.AppendLine("Create an implementation-ready plan for the request below before any coding begins.");
		}
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
