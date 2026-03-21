using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.VersionControl.Models;

public sealed class GitHubRepositoryBrowserItem
{
	[JsonPropertyName("nameWithOwner")]
	public string NameWithOwner { get; set; } = string.Empty;

	[JsonPropertyName("description")]
	public string? Description { get; set; }

	[JsonPropertyName("isPrivate")]
	public bool IsPrivate { get; set; }

	[JsonPropertyName("updatedAt")]
	public DateTimeOffset? UpdatedAt { get; set; }

	[JsonPropertyName("url")]
	public string? Url { get; set; }
}

public sealed class GitHubRepositoryBrowserResult
{
	public bool IsGitHubCliAvailable { get; set; }

	public bool IsAuthenticated { get; set; }

	public string? ErrorMessage { get; set; }

	public List<GitHubRepositoryBrowserItem> Repositories { get; set; } = [];
}
