using System.Diagnostics;
using System.Text;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

public interface ISystemCommandRunner
{
	Task<CommandExecutionResult> RunAsync(
		string command,
		string workingDirectory,
		Func<string, bool, Task>? onOutput = null,
		CancellationToken cancellationToken = default);

	Task<CommandLaunchResult> LaunchDetachedAsync(
		string command,
		string workingDirectory,
		int delaySeconds = 0,
		CancellationToken cancellationToken = default);
}

public sealed record CommandExecutionResult(bool Success, int ExitCode, string? ErrorMessage = null);

public sealed record CommandLaunchResult(bool Success, string? ErrorMessage = null);

public class SystemCommandRunner : ISystemCommandRunner
{
	public async Task<CommandExecutionResult> RunAsync(
		string command,
		string workingDirectory,
		Func<string, bool, Task>? onOutput = null,
		CancellationToken cancellationToken = default)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = PlatformHelper.ShellExecutable,
			WorkingDirectory = workingDirectory
		};

		startInfo.ArgumentList.Add(PlatformHelper.ShellArgumentPrefix);
		startInfo.ArgumentList.Add(command);
		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		try
		{
			using var process = new Process { StartInfo = startInfo };
			process.Start();

			var stderrBuffer = new StringBuilder();
			var stdoutTask = ForwardStreamAsync(process.StandardOutput, isError: false, onOutput, null, cancellationToken);
			var stderrTask = ForwardStreamAsync(process.StandardError, isError: true, onOutput, stderrBuffer, cancellationToken);

			await process.WaitForExitAsync(cancellationToken);
			await Task.WhenAll(stdoutTask, stderrTask);

			return process.ExitCode == 0
				? new CommandExecutionResult(true, process.ExitCode)
				: new CommandExecutionResult(false, process.ExitCode, BuildErrorMessage(command, process.ExitCode, stderrBuffer));
		}
		catch (OperationCanceledException)
		{
			return new CommandExecutionResult(false, -1, "The command was canceled.");
		}
		catch (Exception ex)
		{
			return new CommandExecutionResult(false, -1, ex.Message);
		}
	}

	public async Task<CommandLaunchResult> LaunchDetachedAsync(
		string command,
		string workingDirectory,
		int delaySeconds = 0,
		CancellationToken cancellationToken = default)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = PlatformHelper.ShellExecutable,
			WorkingDirectory = workingDirectory
		};

		startInfo.ArgumentList.Add(PlatformHelper.ShellArgumentPrefix);
		startInfo.ArgumentList.Add(BuildDetachedCommand(command, delaySeconds));
		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		try
		{
			using var process = new Process { StartInfo = startInfo };
			process.Start();

			var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);

			var output = await outputTask;
			var error = await errorTask;

			if (process.ExitCode == 0)
			{
				return new CommandLaunchResult(true);
			}

			var message = string.IsNullOrWhiteSpace(error) ? output : error;
			return new CommandLaunchResult(false, string.IsNullOrWhiteSpace(message)
				? $"Failed to launch detached command: {command}"
				: message.Trim());
		}
		catch (OperationCanceledException)
		{
			return new CommandLaunchResult(false, "Launching the restart command was canceled.");
		}
		catch (Exception ex)
		{
			return new CommandLaunchResult(false, ex.Message);
		}
	}

	private static async Task ForwardStreamAsync(
		StreamReader reader,
		bool isError,
		Func<string, bool, Task>? onOutput,
		StringBuilder? captureBuffer,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			var line = await reader.ReadLineAsync(cancellationToken);
			if (line is null)
			{
				break;
			}

			if (captureBuffer is not null)
			{
				captureBuffer.AppendLine(line);
			}

			if (onOutput is not null)
			{
				await onOutput(line, isError);
			}
		}
	}

	private static string BuildDetachedCommand(string command, int delaySeconds)
	{
		if (PlatformHelper.IsWindows)
		{
			var delayedCommand = delaySeconds > 0
				? $"ping 127.0.0.1 -n {delaySeconds + 1} > nul && {command}"
				: command;
			var escapedCommand = delayedCommand.Replace("\"", "\\\"", StringComparison.Ordinal);
			return $"start \"\" /b cmd /c \"{escapedCommand}\"";
		}

		var delayed = delaySeconds > 0
			? $"sleep {delaySeconds} && {command}"
			: command;
		var escaped = delayed.Replace("'", "'\\''", StringComparison.Ordinal);
		return $"nohup /bin/sh -lc '{escaped}' >/dev/null 2>&1 &";
	}

	private static string BuildErrorMessage(string command, int exitCode, StringBuilder stderrBuffer)
	{
		var stderr = stderrBuffer.ToString().Trim();
		return string.IsNullOrWhiteSpace(stderr)
			? $"Command exited with code {exitCode}: {command}"
			: stderr;
	}
}
