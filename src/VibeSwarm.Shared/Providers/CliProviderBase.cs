using System.Diagnostics;
using System.Text;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Base class for CLI-based providers with shared execution logic.
/// Providers that primarily operate through CLI commands should inherit from this class.
/// </summary>
public abstract class CliProviderBase : ProviderBase
{
	protected readonly string? ExecutablePath;
	protected readonly string? WorkingDirectory;

	/// <summary>
	/// Default timeout for CLI test commands (version checks, etc.)
	/// </summary>
	protected virtual TimeSpan TestCommandTimeout => TimeSpan.FromSeconds(10);

	/// <summary>
	/// Default timeout for simple prompt commands
	/// </summary>
	protected virtual TimeSpan PromptTimeout => TimeSpan.FromMinutes(2);

	/// <summary>
	/// Interval for reporting initialization progress while waiting for CLI output
	/// </summary>
	protected virtual TimeSpan InitializationCheckInterval => TimeSpan.FromSeconds(5);

	protected CliProviderBase(Guid id, string name, ProviderConnectionMode connectionMode, string? executablePath, string? workingDirectory)
		: base(id, name, connectionMode)
	{
		ExecutablePath = executablePath;
		WorkingDirectory = workingDirectory;
	}

	/// <summary>
	/// Gets the full path to the CLI executable, using platform-specific resolution.
	/// </summary>
	/// <param name="defaultExecutable">The default executable name if no path is configured</param>
	protected string ResolveExecutablePath(string defaultExecutable)
	{
		var basePath = !string.IsNullOrEmpty(ExecutablePath) ? ExecutablePath : defaultExecutable;
		return PlatformHelper.ResolveExecutablePath(basePath, ExecutablePath);
	}

	/// <summary>
	/// Escapes an argument for safe CLI usage using platform-specific escaping.
	/// </summary>
	protected static string EscapeCliArgument(string argument)
	{
		return PlatformHelper.EscapeArgument(argument).Trim('"', '\'');
	}

	/// <summary>
	/// Tests CLI connection by executing a version command.
	/// </summary>
	/// <param name="executablePath">Path to the executable</param>
	/// <param name="providerName">Name of the provider for error messages</param>
	/// <param name="versionArgs">Arguments to get version (default: --version)</param>
	/// <param name="cancellationToken">Cancellation token</param>
	protected async Task<bool> TestCliConnectionAsync(
		string executablePath,
		string providerName,
		string versionArgs = "--version",
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrEmpty(executablePath))
		{
			IsConnected = false;
			LastConnectionError = $"{providerName} executable path is not configured.";
			return false;
		}

		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = executablePath,
				Arguments = versionArgs
			};

			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			if (!string.IsNullOrEmpty(WorkingDirectory))
			{
				startInfo.WorkingDirectory = WorkingDirectory;
			}

			using var process = new Process { StartInfo = startInfo };
			using var timeoutCts = new CancellationTokenSource(TestCommandTimeout);
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			process.Start();

			try
			{
				process.StandardInput.Close();
			}
			catch
			{
				// Ignore if stdin is already closed
			}

			try
			{
				await process.WaitForExitAsync(linkedCts.Token);
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				IsConnected = false;
				LastConnectionError = BuildTimeoutErrorMessage(executablePath, versionArgs, providerName);
				return false;
			}

			var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
			var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);

			IsConnected = process.ExitCode == 0 && !string.IsNullOrEmpty(output);

			if (!IsConnected)
			{
				LastConnectionError = BuildConnectionFailedError(executablePath, versionArgs, process.ExitCode, output, error);
			}
			else
			{
				LastConnectionError = null;
			}

			return IsConnected;
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			IsConnected = false;
			var envPath = Environment.GetEnvironmentVariable("PATH") ?? "not set";
			LastConnectionError = $"Failed to start {providerName} CLI: {ex.Message}. " +
				$"Executable path: '{executablePath}'. " +
				$"Current PATH: {envPath}. " +
				$"If running as a systemd service, ensure the executable is in a standard location " +
				$"or configure the full path to the executable in the provider settings.";
			return false;
		}
		catch (Exception ex)
		{
			IsConnected = false;
			LastConnectionError = $"Unexpected error testing {providerName} CLI connection: {ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}

	/// <summary>
	/// Builds a timeout error message for CLI test commands.
	/// </summary>
	protected virtual string BuildTimeoutErrorMessage(string executablePath, string args, string providerName)
	{
		return $"CLI test timed out after {TestCommandTimeout.TotalSeconds} seconds. Command: {executablePath} {args}\n" +
			"This usually indicates:\n" +
			$"  - The CLI is waiting for authentication (check if '{Path.GetFileNameWithoutExtension(executablePath)}' works in terminal)\n" +
			"  - The CLI is trying to access the network and timing out\n" +
			"  - The process doesn't have access to required environment variables\n" +
			"  - The service account doesn't have permission to run the CLI";
	}

	/// <summary>
	/// Builds a connection failed error message.
	/// </summary>
	protected virtual string BuildConnectionFailedError(string executablePath, string args, int exitCode, string output, string error)
	{
		var errorDetails = new StringBuilder();
		errorDetails.AppendLine($"CLI test failed for command: {executablePath} {args}");
		errorDetails.AppendLine($"Exit code: {exitCode}");

		if (!string.IsNullOrEmpty(error))
		{
			errorDetails.AppendLine($"Error output: {error.Trim()}");
		}

		if (string.IsNullOrEmpty(output))
		{
			errorDetails.AppendLine($"No output received from {args} command.");
		}
		else
		{
			errorDetails.AppendLine($"Output: {output.Trim()}");
		}

		return errorDetails.ToString();
	}

	/// <summary>
	/// Gets the version of the CLI tool.
	/// </summary>
	protected async Task<string> GetCliVersionAsync(string executablePath, string versionArgs = "--version", CancellationToken cancellationToken = default)
	{
		try
		{
			using var timeoutCts = new CancellationTokenSource(TestCommandTimeout);
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			var startInfo = new ProcessStartInfo
			{
				FileName = executablePath,
				Arguments = versionArgs
			};

			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var process = new Process { StartInfo = startInfo };
			process.Start();

			var version = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
			await process.WaitForExitAsync(linkedCts.Token);

			return version.Trim();
		}
		catch (OperationCanceledException)
		{
			return "unknown (timeout)";
		}
		catch
		{
			return "unknown";
		}
	}

	/// <summary>
	/// Executes a simple CLI prompt and returns the response.
	/// </summary>
	protected async Task<PromptResponse> ExecuteSimplePromptAsync(
		string executablePath,
		string args,
		string providerName,
		string? workingDirectory = null,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;

		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = executablePath,
				Arguments = args,
				WorkingDirectory = effectiveWorkingDir
			};

			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var process = new Process { StartInfo = startInfo };
			using var timeoutCts = new CancellationTokenSource(PromptTimeout);
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			process.Start();
			try { process.StandardInput.Close(); } catch { }

			var output = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
			var error = await process.StandardError.ReadToEndAsync(linkedCts.Token);

			try
			{
				await process.WaitForExitAsync(linkedCts.Token);
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				return PromptResponse.Fail($"Request timed out after {PromptTimeout.TotalMinutes} minutes.");
			}

			stopwatch.Stop();

			if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
			{
				return PromptResponse.Fail($"{providerName} CLI returned error: {error}");
			}

			return PromptResponse.Ok(output.Trim(), stopwatch.ElapsedMilliseconds, providerName.ToLowerInvariant());
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			return PromptResponse.Fail($"Failed to start {providerName} CLI: {ex.Message}");
		}
		catch (OperationCanceledException)
		{
			return PromptResponse.Fail("Request was cancelled.");
		}
		catch (Exception ex)
		{
			return PromptResponse.Fail($"Error executing {providerName} CLI: {ex.Message}");
		}
	}

	/// <summary>
	/// Creates and starts a CLI process with standard configuration.
	/// </summary>
	protected Process CreateCliProcess(string executablePath, string args, string? workingDirectory = null)
	{
		var effectiveWorkingDir = workingDirectory ?? WorkingDirectory ?? Environment.CurrentDirectory;

		var startInfo = new ProcessStartInfo
		{
			FileName = executablePath,
			Arguments = args,
			WorkingDirectory = effectiveWorkingDir
		};

		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		if (CurrentEnvironmentVariables != null)
		{
			foreach (var kvp in CurrentEnvironmentVariables)
			{
				startInfo.Environment[kvp.Key] = kvp.Value;
			}
		}

		return new Process { StartInfo = startInfo };
	}

	/// <summary>
	/// Reports process startup progress to the UI.
	/// </summary>
	protected void ReportProcessStarted(int processId, IProgress<ExecutionProgress>? progress, string? fullCommand = null)
	{
		progress?.Report(new ExecutionProgress
		{
			CurrentMessage = "CLI process started successfully",
			ProcessId = processId,
			CommandUsed = fullCommand,
			IsStreaming = false
		});

		progress?.Report(new ExecutionProgress
		{
			OutputLine = $"[VibeSwarm] Process started (PID: {processId}). Waiting for CLI to initialize...",
			IsStreaming = true
		});
	}

	/// <summary>
	/// Creates an initialization monitoring task that reports progress while waiting for first output.
	/// </summary>
	protected Task CreateInitializationMonitorAsync(
		Func<bool> hasOutputReceived,
		IProgress<ExecutionProgress>? progress,
		CancellationToken cancellationToken)
	{
		var lastOutputTime = DateTime.UtcNow;
		var warningsSent = 0;

		return Task.Run(async () =>
		{
			try
			{
				while (!cancellationToken.IsCancellationRequested)
				{
					await Task.Delay(InitializationCheckInterval, cancellationToken);

					if (hasOutputReceived())
					{
						break;
					}

					var waitTime = DateTime.UtcNow - lastOutputTime;
					warningsSent++;

					var waitMessage = warningsSent switch
					{
						1 => $"[VibeSwarm] Still initializing... (waited {waitTime.TotalSeconds:F0}s).",
						2 => $"[VibeSwarm] Still waiting for response... (waited {waitTime.TotalSeconds:F0}s).",
						_ => $"[VibeSwarm] Still waiting ({waitTime.TotalSeconds:F0}s)... Process is running."
					};

					progress?.Report(new ExecutionProgress
					{
						OutputLine = waitMessage,
						IsStreaming = true
					});

					progress?.Report(new ExecutionProgress
					{
						CurrentMessage = $"Waiting for CLI response ({waitTime.TotalSeconds:F0}s)...",
						IsStreaming = false
					});
				}
			}
			catch (OperationCanceledException)
			{
				// Expected when process completes or is cancelled
			}
		}, cancellationToken);
	}

	/// <summary>
	/// Waits for a process to exit with proper cancellation handling.
	/// </summary>
	protected async Task WaitForProcessExitAsync(
		Process process,
		CancellationTokenSource initMonitorCts,
		CancellationToken cancellationToken)
	{
		try
		{
			await process.WaitForExitAsync(cancellationToken);
			initMonitorCts.Cancel();
		}
		catch (OperationCanceledException)
		{
			initMonitorCts.Cancel();
			PlatformHelper.TryKillProcessTree(process.Id);
			throw;
		}
	}

	/// <summary>
	/// Waits for output/error streams to complete with a timeout.
	/// </summary>
	protected async Task WaitForOutputStreamsAsync(
		TaskCompletionSource<bool> outputComplete,
		TaskCompletionSource<bool> errorComplete,
		TimeSpan timeout = default)
	{
		if (timeout == default)
		{
			timeout = TimeSpan.FromSeconds(10);
		}

		var outputTimeout = Task.Delay(timeout, CancellationToken.None);
		await Task.WhenAny(Task.WhenAll(outputComplete.Task, errorComplete.Task), outputTimeout);
	}

	/// <summary>
	/// Generates a summary from execution output by looking for action-oriented statements.
	/// </summary>
	protected static string GenerateSummaryFromOutput(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
			return string.Empty;

		var lines = output.Split('\n');
		var significantActions = new List<string>();

		foreach (var line in lines)
		{
			var trimmed = line.Trim();

			// Skip empty lines and JSON
			if (string.IsNullOrWhiteSpace(trimmed)) continue;
			if (trimmed.StartsWith("{") || trimmed.StartsWith("[")) continue;
			if (trimmed.Length < 10) continue;

			// Look for action-oriented statements
			if (trimmed.Contains("created", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("modified", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("updated", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("added", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("removed", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("fixed", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("implemented", StringComparison.OrdinalIgnoreCase) ||
				trimmed.Contains("refactored", StringComparison.OrdinalIgnoreCase))
			{
				if (trimmed.Length < 200)
				{
					significantActions.Add(trimmed);
				}
			}
		}

		if (significantActions.Count > 0)
		{
			return string.Join("; ", significantActions.Take(3));
		}

		// Fallback: return first meaningful line
		foreach (var line in lines)
		{
			var trimmed = line.Trim();
			if (!string.IsNullOrWhiteSpace(trimmed) &&
				!trimmed.StartsWith("{") &&
				!trimmed.StartsWith("[") &&
				trimmed.Length >= 20 &&
				trimmed.Length <= 200)
			{
				return trimmed;
			}
		}

		return string.Empty;
	}
}
