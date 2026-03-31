namespace VibeSwarm.Shared.VersionControl.Models;

public sealed class GitCommitOptions
{
	public string? AuthorName { get; init; }
	public string? AuthorEmail { get; init; }
	public IReadOnlyList<string> MessageTrailers { get; init; } = Array.Empty<string>();
}
