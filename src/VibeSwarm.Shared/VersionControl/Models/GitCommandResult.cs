namespace VibeSwarm.Shared.VersionControl.Models;

/// <summary>
/// Result of executing a git command.
/// </summary>
public sealed class GitCommandResult
{
	/// <summary>
	/// The exit code from the git process.
	/// </summary>
	public int ExitCode { get; init; }

	/// <summary>
	/// The standard output from the command.
	/// </summary>
	public string Output { get; init; } = string.Empty;

	/// <summary>
	/// The standard error output from the command.
	/// </summary>
	public string Error { get; init; } = string.Empty;

	/// <summary>
	/// Gets whether the command succeeded (exit code 0).
	/// </summary>
	public bool Success => ExitCode == 0;
}
