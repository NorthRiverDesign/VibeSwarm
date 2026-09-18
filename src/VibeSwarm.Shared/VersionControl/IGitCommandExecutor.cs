using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Interface for executing git commands.
/// </summary>
public interface IGitCommandExecutor
{
	/// <summary>
	/// Executes a git command asynchronously.
	/// </summary>
	/// <param name="arguments">The git command arguments.</param>
	/// <param name="timeoutSeconds">Timeout in seconds (default 30).</param>
	Task<GitCommandResult> ExecuteAsync(
		string arguments,
		string workingDirectory,
		CancellationToken cancellationToken = default,
		int timeoutSeconds = 30);

	/// <summary>
	/// Executes an arbitrary command asynchronously (not limited to git).
	/// </summary>
	/// <param name="command">The command to execute (e.g., "gh", "npm").</param>
	/// <param name="arguments">The command arguments.</param>
	/// <param name="timeoutSeconds">Timeout in seconds (default 30).</param>
	Task<GitCommandResult> ExecuteRawAsync(
		string command,
		string arguments,
		string workingDirectory,
		CancellationToken cancellationToken = default,
		int timeoutSeconds = 30);
}
