using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Manages CLI process execution with proper lifecycle management, streaming output, and cross-platform support.
/// Designed to handle multiple concurrent CLI processes for job execution.
/// </summary>
public class CliProcessManager : IDisposable
{
	private readonly ConcurrentDictionary<int, ManagedProcess> _processes = new();
	private readonly Action<string>? _logger;
	private bool _disposed;

	/// <summary>
	/// Event fired when a line of output is received from a process
	/// </summary>
	public event Action<int, string, bool>? OutputReceived; // processId, line, isError

	/// <summary>
	/// Event fired when a process exits
	/// </summary>
	public event Action<int, int>? ProcessExited; // processId, exitCode

	public CliProcessManager(Action<string>? logger = null)
	{
		_logger = logger;
	}

	/// <summary>
	/// Information about a managed CLI process
	/// </summary>
	public class ManagedProcess
	{
		public int ProcessId { get; init; }
		public Process Process { get; init; } = null!;
		public DateTime StartTime { get; init; }
		public DateTime? LastOutputTime { get; set; }
		public StringBuilder OutputBuffer { get; } = new();
		public StringBuilder ErrorBuffer { get; } = new();
		public TaskCompletionSource<bool> OutputComplete { get; } = new();
		public TaskCompletionSource<bool> ErrorComplete { get; } = new();
		public CancellationTokenSource? CancellationTokenSource { get; set; }
		public bool IsCompleted { get; set; }
		public int? ExitCode { get; set; }
	}

	/// <summary>
	/// Options for starting a CLI process
	/// </summary>
	public class ProcessOptions
	{
		/// <summary>
		/// The executable path or name (will be resolved based on platform)
		/// </summary>
		public string Executable { get; set; } = string.Empty;

		/// <summary>
		/// Arguments to pass to the executable
		/// </summary>
		public string Arguments { get; set; } = string.Empty;

		/// <summary>
		/// Working directory for the process
		/// </summary>
		public string? WorkingDirectory { get; set; }

		/// <summary>
		/// Environment variables to set for the process
		/// </summary>
		public Dictionary<string, string>? EnvironmentVariables { get; set; }

		/// <summary>
		/// Timeout for the process (null = no timeout)
		/// </summary>
		public TimeSpan? Timeout { get; set; }

		/// <summary>
		/// Whether to capture output in memory (in addition to streaming events)
		/// </summary>
		public bool CaptureOutput { get; set; } = true;

		/// <summary>
		/// Optional callback for each line of stdout
		/// </summary>
		public Action<string>? OnOutput { get; set; }

		/// <summary>
		/// Optional callback for each line of stderr
		/// </summary>
		public Action<string>? OnError { get; set; }
	}

	/// <summary>
	/// Result of a CLI process execution
	/// </summary>
	public class ProcessResult
	{
		public int ProcessId { get; set; }
		public int ExitCode { get; set; }
		public bool Success => ExitCode == 0;
		public string Output { get; set; } = string.Empty;
		public string Error { get; set; } = string.Empty;
		public TimeSpan Duration { get; set; }
		public bool WasCancelled { get; set; }
		public bool TimedOut { get; set; }
		public string? FailureReason { get; set; }
	}

	/// <summary>
	/// Starts a CLI process and returns immediately with the process ID
	/// </summary>
	public async Task<(int ProcessId, ManagedProcess ManagedProcess)?> StartProcessAsync(
		ProcessOptions options,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		var executablePath = PlatformHelper.ResolveExecutablePath(options.Executable);
		_logger?.Invoke($"Starting CLI process: {executablePath} {options.Arguments}");
		_logger?.Invoke($"Platform: {PlatformHelper.OsDescription}");

		var startInfo = new ProcessStartInfo
		{
			FileName = executablePath,
			Arguments = options.Arguments
		};

		// Only set working directory if explicitly provided
		// This allows systemd services to work without a specific working directory
		if (!string.IsNullOrEmpty(options.WorkingDirectory))
		{
			startInfo.WorkingDirectory = options.WorkingDirectory;
		}

		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		// Add custom environment variables
		if (options.EnvironmentVariables != null)
		{
			foreach (var kvp in options.EnvironmentVariables)
			{
				startInfo.Environment[kvp.Key] = kvp.Value;
			}
		}

		var process = new Process { StartInfo = startInfo };

		try
		{
			process.Start();
		}
		catch (System.ComponentModel.Win32Exception ex)
		{
			_logger?.Invoke($"Failed to start process: {ex.Message}");
			process.Dispose();
			return null;
		}

		var managedProcess = new ManagedProcess
		{
			ProcessId = process.Id,
			Process = process,
			StartTime = DateTime.UtcNow
		};

		if (options.Timeout.HasValue)
		{
			managedProcess.CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			managedProcess.CancellationTokenSource.CancelAfter(options.Timeout.Value);
		}

		_processes[process.Id] = managedProcess;

		// Set up output handling
		process.OutputDataReceived += (sender, e) =>
		{
			if (e.Data == null)
			{
				managedProcess.OutputComplete.TrySetResult(true);
				return;
			}

			managedProcess.LastOutputTime = DateTime.UtcNow;

			if (options.CaptureOutput)
			{
				lock (managedProcess.OutputBuffer)
				{
					managedProcess.OutputBuffer.AppendLine(e.Data);
				}
			}

			options.OnOutput?.Invoke(e.Data);
			OutputReceived?.Invoke(process.Id, e.Data, false);
		};

		process.ErrorDataReceived += (sender, e) =>
		{
			if (e.Data == null)
			{
				managedProcess.ErrorComplete.TrySetResult(true);
				return;
			}

			managedProcess.LastOutputTime = DateTime.UtcNow;

			if (options.CaptureOutput)
			{
				lock (managedProcess.ErrorBuffer)
				{
					managedProcess.ErrorBuffer.AppendLine(e.Data);
				}
			}

			options.OnError?.Invoke(e.Data);
			OutputReceived?.Invoke(process.Id, e.Data, true);
		};

		// Close stdin to signal no input is coming
		try
		{
			process.StandardInput.Close();
		}
		catch
		{
			// Ignore if already closed
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		_logger?.Invoke($"Process started with PID {process.Id}");

		return (process.Id, managedProcess);
	}

	/// <summary>
	/// Runs a CLI process to completion and returns the result
	/// </summary>
	public async Task<ProcessResult> RunAsync(
		ProcessOptions options,
		CancellationToken cancellationToken = default)
	{
		var startResult = await StartProcessAsync(options, cancellationToken);

		if (startResult == null)
		{
			return new ProcessResult
			{
				ProcessId = -1,
				ExitCode = -1,
				FailureReason = $"Failed to start process: {options.Executable}"
			};
		}

		var (processId, managedProcess) = startResult.Value;

		return await WaitForExitAsync(processId, managedProcess.CancellationTokenSource?.Token ?? cancellationToken);
	}

	/// <summary>
	/// Waits for a process to complete and returns the result
	/// </summary>
	public async Task<ProcessResult> WaitForExitAsync(
		int processId,
		CancellationToken cancellationToken = default)
	{
		if (!_processes.TryGetValue(processId, out var managedProcess))
		{
			return new ProcessResult
			{
				ProcessId = processId,
				ExitCode = -1,
				FailureReason = "Process not found"
			};
		}

		var result = new ProcessResult { ProcessId = processId };
		var startTime = managedProcess.StartTime;
		var process = managedProcess.Process;

		try
		{
			var effectiveCancellation = managedProcess.CancellationTokenSource?.Token ?? cancellationToken;
			await process.WaitForExitAsync(effectiveCancellation);
		}
		catch (OperationCanceledException)
		{
			_logger?.Invoke($"Process {processId} cancelled, terminating...");

			// Determine if this was a timeout or user cancellation
			result.TimedOut = managedProcess.CancellationTokenSource?.IsCancellationRequested == true
							  && !cancellationToken.IsCancellationRequested;
			result.WasCancelled = !result.TimedOut;

			// Kill the process tree
			await KillProcessAsync(processId);

			result.ExitCode = -1;
			result.FailureReason = result.TimedOut ? "Process timed out" : "Process was cancelled";
		}

		// Wait for output streams to complete (with timeout)
		try
		{
			var outputTimeout = Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
			await Task.WhenAny(
				Task.WhenAll(managedProcess.OutputComplete.Task, managedProcess.ErrorComplete.Task),
				outputTimeout);
		}
		catch
		{
			// Ignore timeout on output completion
		}

		// Gather results
		result.Duration = DateTime.UtcNow - startTime;

		if (!result.WasCancelled && !result.TimedOut)
		{
			try
			{
				result.ExitCode = process.ExitCode;
			}
			catch
			{
				result.ExitCode = -1;
			}
		}

		lock (managedProcess.OutputBuffer)
		{
			result.Output = managedProcess.OutputBuffer.ToString();
		}
		lock (managedProcess.ErrorBuffer)
		{
			result.Error = managedProcess.ErrorBuffer.ToString();
		}

		managedProcess.IsCompleted = true;
		managedProcess.ExitCode = result.ExitCode;

		ProcessExited?.Invoke(processId, result.ExitCode);

		_logger?.Invoke($"Process {processId} completed with exit code {result.ExitCode} in {result.Duration.TotalSeconds:F1}s");

		return result;
	}

	/// <summary>
	/// Kills a process and its entire process tree
	/// </summary>
	public async Task<bool> KillProcessAsync(int processId)
	{
		if (!_processes.TryGetValue(processId, out var managedProcess))
		{
			return false;
		}

		_logger?.Invoke($"Killing process {processId}...");

		// Cancel any linked cancellation token
		try
		{
			managedProcess.CancellationTokenSource?.Cancel();
		}
		catch
		{
			// Ignore
		}

		// Use platform-specific kill method
		var success = PlatformHelper.TryKillProcessTree(processId, _logger);

		if (success)
		{
			managedProcess.IsCompleted = true;
			managedProcess.OutputComplete.TrySetResult(true);
			managedProcess.ErrorComplete.TrySetResult(true);
		}

		return success;
	}

	/// <summary>
	/// Gets information about a managed process
	/// </summary>
	public ManagedProcess? GetProcess(int processId)
	{
		_processes.TryGetValue(processId, out var process);
		return process;
	}

	/// <summary>
	/// Gets all currently managed processes
	/// </summary>
	public IEnumerable<ManagedProcess> GetAllProcesses()
	{
		return _processes.Values;
	}

	/// <summary>
	/// Gets the number of running processes
	/// </summary>
	public int RunningProcessCount => _processes.Values.Count(p => !p.IsCompleted);

	/// <summary>
	/// Checks if a process is still running (outputting data)
	/// </summary>
	public bool IsProcessActive(int processId, TimeSpan stallThreshold)
	{
		if (!_processes.TryGetValue(processId, out var process))
			return false;

		if (process.IsCompleted)
			return false;

		// Check if process has output recently
		if (process.LastOutputTime.HasValue)
		{
			return DateTime.UtcNow - process.LastOutputTime.Value < stallThreshold;
		}

		// No output yet, check if process started recently
		return DateTime.UtcNow - process.StartTime < stallThreshold;
	}

	/// <summary>
	/// Removes a completed process from tracking
	/// </summary>
	public bool RemoveProcess(int processId)
	{
		if (_processes.TryRemove(processId, out var process))
		{
			try
			{
				process.CancellationTokenSource?.Dispose();
				process.Process?.Dispose();
			}
			catch
			{
				// Ignore disposal errors
			}
			return true;
		}
		return false;
	}

	/// <summary>
	/// Cleans up all completed processes
	/// </summary>
	public void CleanupCompletedProcesses()
	{
		var completedIds = _processes
			.Where(kvp => kvp.Value.IsCompleted)
			.Select(kvp => kvp.Key)
			.ToList();

		foreach (var id in completedIds)
		{
			RemoveProcess(id);
		}

		_logger?.Invoke($"Cleaned up {completedIds.Count} completed processes");
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(CliProcessManager));
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;

		// Kill all running processes
		foreach (var kvp in _processes)
		{
			try
			{
				kvp.Value.CancellationTokenSource?.Cancel();
				if (kvp.Value.Process != null && !kvp.Value.Process.HasExited)
				{
					PlatformHelper.TryKillProcessTree(kvp.Key, _logger);
				}
				kvp.Value.CancellationTokenSource?.Dispose();
				kvp.Value.Process?.Dispose();
			}
			catch
			{
				// Ignore errors during cleanup
			}
		}

		_processes.Clear();
	}
}
