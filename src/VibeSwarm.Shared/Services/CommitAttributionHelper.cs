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
