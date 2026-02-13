using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Shared.VersionControl;

/// <summary>
/// Executes git commands via the system git executable.
/// </summary>
public sealed class GitCommandExecutor : IGitCommandExecutor
{
	/// <inheritdoc />
	public async Task<GitCommandResult> ExecuteAsync(
		string arguments,
		string workingDirectory,
		CancellationToken cancellationToken = default,
		int timeoutSeconds = 30)
	{
		var command = PlatformHelper.IsWindows ? "git.exe" : "git";
		return await ExecuteRawAsync(command, arguments, workingDirectory, cancellationToken, timeoutSeconds);
	}

	/// <inheritdoc />
	public async Task<GitCommandResult> ExecuteRawAsync(
		string command,
		string arguments,
		string workingDirectory,
		CancellationToken cancellationToken = default,
		int timeoutSeconds = 30)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = command,
			Arguments = arguments,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

		using var process = new Process { StartInfo = startInfo };
		var outputBuilder = new StringBuilder();
		var errorBuilder = new StringBuilder();

		process.OutputDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				outputBuilder.AppendLine(e.Data);
			}
		};

		process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				errorBuilder.AppendLine(e.Data);
			}
		};

		try
		{
			process.Start();
		}
		catch (Win32Exception ex)
		{
			// Executable not found or not accessible
			return new GitCommandResult
			{
				ExitCode = -1,
				Output = string.Empty,
				Error = $"Failed to start {command}: {ex.Message}"
			};
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

		try
		{
			await process.WaitForExitAsync(timeoutCts.Token);
		}
		catch (OperationCanceledException)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch
			{
				// Ignore errors when killing process
			}

			throw;
		}

		return new GitCommandResult
		{
			ExitCode = process.ExitCode,
			Output = outputBuilder.ToString(),
			Error = errorBuilder.ToString()
		};
	}
}
