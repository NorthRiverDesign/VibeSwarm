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
	/// <param name="workingDirectory">The working directory for the command.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <param name="timeoutSeconds">Timeout in seconds (default 30).</param>
	/// <returns>The command result.</returns>
	Task<GitCommandResult> ExecuteAsync(
		string arguments,
		string workingDirectory,
		CancellationToken cancellationToken = default,
		int timeoutSeconds = 30);
}
