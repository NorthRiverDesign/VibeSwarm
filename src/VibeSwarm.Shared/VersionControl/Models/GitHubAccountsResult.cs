namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// The accounts a new GitHub repository can be created under: the GitHub CLI's signed-in
/// user and the organizations it belongs to.
/// </summary>
public sealed class GitHubAccountsResult
{
	public bool IsGitHubCliAvailable { get; set; }

	public bool IsAuthenticated { get; set; }

	public string? AuthenticatedUser { get; set; }

	/// <summary>Organization logins, alphabetical, without the user's own account.</summary>
	public List<string> Organizations { get; set; } = [];

	public string? ErrorMessage { get; set; }
}
