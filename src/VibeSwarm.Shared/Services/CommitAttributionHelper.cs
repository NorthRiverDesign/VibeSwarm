using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.Services;

public static class CommitAttributionHelper
{
	public const string CopilotName = "Copilot";
	public const string CopilotEmail = "223556219+Copilot@users.noreply.github.com";
	public const string ClaudeName = "Claude";
	public const string ClaudeEmail = "noreply@anthropic.com";
	public const string OpenCodeName = "OpenCode";
	public const string OpenCodeEmail = "noreply@opencode.ai";

	public static IReadOnlyList<string> BuildPromptRules(ProviderType? providerType, bool enableCommitAttribution)
	{
		if (providerType == null)
		{
			return Array.Empty<string>();
		}

		if (!enableCommitAttribution)
		{
			return
			[
				"If you create git commits yourself, do not add provider attribution or provider-specific trailers.",
				"Use the repository's existing git identity instead of a provider-specific author."
			];
		}

		return providerType.Value switch
		{
			ProviderType.Copilot =>
			[
				$"If you create git commits yourself, add the trailer `{BuildCoAuthorTrailer(CopilotName, CopilotEmail)}`."
			],
			ProviderType.Claude =>
			[
				$"If you create git commits yourself, use the author `{ClaudeName} <{ClaudeEmail}>`."
			],
			ProviderType.OpenCode =>
			[
				$"If you create git commits yourself, use the author `{OpenCodeName} <{OpenCodeEmail}>`."
			],
			_ => Array.Empty<string>()
		};
	}

	public static GitCommitOptions? BuildGitCommitOptions(ProviderType? providerType, bool enableCommitAttribution)
	{
		if (!enableCommitAttribution || providerType == null)
		{
			return null;
		}

		return providerType.Value switch
		{
			ProviderType.Copilot => new GitCommitOptions
			{
				MessageTrailers = [BuildCoAuthorTrailer(CopilotName, CopilotEmail)]
			},
			ProviderType.Claude => new GitCommitOptions
			{
				AuthorName = ClaudeName,
				AuthorEmail = ClaudeEmail
			},
			ProviderType.OpenCode => new GitCommitOptions
			{
				AuthorName = OpenCodeName,
				AuthorEmail = OpenCodeEmail
			},
			_ => null
		};
	}

	public static string BuildCoAuthorTrailer(string name, string email) => $"Co-authored-by: {name} <{email}>";
}
