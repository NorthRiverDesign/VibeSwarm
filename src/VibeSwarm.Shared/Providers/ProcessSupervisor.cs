using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Supervises CLI processes with health monitoring, automatic restart capabilities,
/// and resource management for long-running agent operations.
/// </summary>
public class ProcessSupervisor : IDisposable
{
	private readonly ILogger<ProcessSupervisor>? _logger;
	private readonly ConcurrentDictionary<Guid, SupervisedProcess> _processes = new();
	private readonly Timer _healthCheckTimer;
	private bool _disposed;

	/// <summary>
	/// How often to check process health
	/// </summary>
	public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Maximum memory usage in MB before a process is considered unhealthy
	/// </summary>
	public long MaxMemoryMb { get; set; } = 2048;

	/// <summary>
	/// Time without output before a process is considered stalled
	/// </summary>
	public TimeSpan StallThreshold { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Event fired when a process becomes unhealthy
	/// </summary>
	public event Action<Guid, ProcessHealthStatus>? ProcessUnhealthy;

	/// <summary>
	/// Event fired when a process exits unexpectedly
	/// </summary>
	public event Action<Guid, int>? ProcessExitedUnexpectedly;

	/// <summary>
	/// Event fired when a process is restarted
	/// </summary>
	public event Action<Guid, int, int>? ProcessRestarted; // jobId, oldPid, newPid

	public ProcessSupervisor(ILogger<ProcessSupervisor>? logger = null)
	{
		_logger = logger;
		_healthCheckTimer = new Timer(CheckProcessHealthCallback, null, HealthCheckInterval, HealthCheckInterval);
	}

	/// <summary>
	/// Starts supervising a process for a job
	/// </summary>
	public async Task<SupervisedProcess?> StartProcessAsync(
		Guid jobId,
		ProcessStartOptions options,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		var executablePath = PlatformHelper.ResolveExecutablePath(options.Executable);
		_logger?.LogInformation("Starting supervised process for job {JobId}: {Executable}",
			jobId, executablePath);

		var startInfo = new ProcessStartInfo
		{
			FileName = executablePath,
			Arguments = options.Arguments,
			WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory
		};

		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		if (options.EnvironmentVariables != null)
		{
			foreach (var kvp in options.EnvironmentVariables)
			{
				startInfo.Environment[kvp.Key] = kvp.Value;
			}
		}

		var process = new Process { StartInfo = startInfo };
		var supervisedProcess = new SupervisedProcess
		{
			JobId = jobId,
			Options = options,
			StartTime = DateTime.UtcNow
		};

		try
		{
			process.Start();
			supervisedProcess.Process = process;
			supervisedProcess.ProcessId = process.Id;

			// Set up output handling
			SetupOutputHandling(supervisedProcess, process);

			// Close stdin
			try { process.StandardInput.Close(); }
			catch { /* Ignore */ }

			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			_processes[jobId] = supervisedProcess;

			_logger?.LogInformation("Supervised process started for job {JobId} with PID {ProcessId}",
				jobId, process.Id);

			return supervisedProcess;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Failed to start supervised process for job {JobId}", jobId);
			process.Dispose();
			return null;
		}
	}

	/// <summary>
	/// Gets a supervised process by job ID
	/// </summary>
	public SupervisedProcess? GetProcess(Guid jobId)
	{
		_processes.TryGetValue(jobId, out var process);
		return process;
	}

	/// <summary>
	/// Checks the health of a supervised process
	/// </summary>
	public ProcessHealthStatus CheckHealth(Guid jobId)
	{
		if (!_processes.TryGetValue(jobId, out var supervised))
		{
			return new ProcessHealthStatus
			{
				JobId = jobId,
				IsHealthy = false,
				Reason = "Process not found"
			};
		}

		return CheckProcessHealth(supervised);
	}

	/// <summary>
	/// Stops and removes a supervised process
	/// </summary>
	public async Task<bool> StopProcessAsync(Guid jobId, bool graceful = true, TimeSpan? gracefulTimeout = null)
	{
		if (!_processes.TryRemove(jobId, out var supervised))
		{
			return false;
		}

		return await TerminateProcessAsync(supervised, graceful, gracefulTimeout ?? TimeSpan.FromSeconds(10));
	}

	/// <summary>
	/// Restarts a supervised process
	/// </summary>
	public async Task<bool> RestartProcessAsync(Guid jobId, CancellationToken cancellationToken = default)
	{
		if (!_processes.TryGetValue(jobId, out var supervised))
		{
			_logger?.LogWarning("Cannot restart process for job {JobId} - not found", jobId);
			return false;
		}

		var oldPid = supervised.ProcessId;

		// Stop the existing process
		await TerminateProcessAsync(supervised, true, TimeSpan.FromSeconds(5));

		// Check restart limit
		supervised.RestartCount++;
		if (supervised.Options.MaxRestarts > 0 && supervised.RestartCount > supervised.Options.MaxRestarts)
		{
			_logger?.LogWarning("Process for job {JobId} exceeded restart limit ({Count}/{Max})",
				jobId, supervised.RestartCount, supervised.Options.MaxRestarts);
			_processes.TryRemove(jobId, out _);
			return false;
		}

		// Wait before restart
		await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

		// Start a new process
		var startInfo = new ProcessStartInfo
		{
			FileName = PlatformHelper.ResolveExecutablePath(supervised.Options.Executable),
			Arguments = supervised.Options.Arguments,
			WorkingDirectory = supervised.Options.WorkingDirectory ?? Environment.CurrentDirectory
		};

		PlatformHelper.ConfigureForCrossPlatform(startInfo);

		if (supervised.Options.EnvironmentVariables != null)
		{
			foreach (var kvp in supervised.Options.EnvironmentVariables)
			{
				startInfo.Environment[kvp.Key] = kvp.Value;
			}
		}

		try
		{
			var newProcess = new Process { StartInfo = startInfo };
			newProcess.Start();

			supervised.Process?.Dispose();
			supervised.Process = newProcess;
			supervised.ProcessId = newProcess.Id;
			supervised.LastRestartTime = DateTime.UtcNow;
			supervised.LastOutputTime = null;
			supervised.IsCompleted = false;

			SetupOutputHandling(supervised, newProcess);

			try { newProcess.StandardInput.Close(); }
			catch { }

			newProcess.BeginOutputReadLine();
			newProcess.BeginErrorReadLine();

			_logger?.LogInformation("Restarted process for job {JobId}: PID {OldPid} -> {NewPid} (restart {Count})",
				jobId, oldPid, newProcess.Id, supervised.RestartCount);

			ProcessRestarted?.Invoke(jobId, oldPid, newProcess.Id);
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Failed to restart process for job {JobId}", jobId);
			_processes.TryRemove(jobId, out _);
			return false;
		}
	}

	/// <summary>
	/// Waits for a process to complete
	/// </summary>
	public async Task<ProcessCompletionResult> WaitForCompletionAsync(
		Guid jobId,
		CancellationToken cancellationToken = default)
	{
		if (!_processes.TryGetValue(jobId, out var supervised))
		{
			return new ProcessCompletionResult
			{
				Success = false,
				FailureReason = "Process not found"
			};
		}

		try
		{
			await supervised.Process!.WaitForExitAsync(cancellationToken);

			supervised.IsCompleted = true;
			supervised.ExitCode = supervised.Process.ExitCode;

			// Wait for output streams
			try
			{
				var timeout = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
				await Task.WhenAny(
					Task.WhenAll(supervised.OutputComplete.Task, supervised.ErrorComplete.Task),
					timeout);
			}
			catch { }

			var duration = DateTime.UtcNow - supervised.StartTime;

			_processes.TryRemove(jobId, out _);

			return new ProcessCompletionResult
			{
				Success = supervised.Process.ExitCode == 0,
				ExitCode = supervised.Process.ExitCode,
				Duration = duration,
				Output = supervised.OutputBuffer.ToString(),
				Error = supervised.ErrorBuffer.ToString(),
				RestartCount = supervised.RestartCount
			};
		}
		catch (OperationCanceledException)
		{
			return new ProcessCompletionResult
			{
				Success = false,
				WasCancelled = true,
				FailureReason = "Process was cancelled"
			};
		}
	}

	/// <summary>
	/// Gets all supervised processes
	/// </summary>
	public IEnumerable<SupervisedProcess> GetAllProcesses()
	{
		return _processes.Values;
	}

	/// <summary>
	/// Gets count of running processes
	/// </summary>
	public int RunningProcessCount => _processes.Count(p => !p.Value.IsCompleted);

	private void SetupOutputHandling(SupervisedProcess supervised, Process process)
	{
		process.OutputDataReceived += (sender, e) =>
		{
			if (e.Data == null)
			{
				supervised.OutputComplete.TrySetResult(true);
				return;
			}

			supervised.LastOutputTime = DateTime.UtcNow;
			lock (supervised.OutputBuffer)
			{
				supervised.OutputBuffer.AppendLine(e.Data);
			}
			supervised.OnOutput?.Invoke(e.Data, false);
		};

		process.ErrorDataReceived += (sender, e) =>
		{
			if (e.Data == null)
			{
				supervised.ErrorComplete.TrySetResult(true);
				return;
			}

			supervised.LastOutputTime = DateTime.UtcNow;
			lock (supervised.ErrorBuffer)
			{
				supervised.ErrorBuffer.AppendLine(e.Data);
			}
			supervised.OnOutput?.Invoke(e.Data, true);
		};
	}

	private void CheckProcessHealthCallback(object? state)
	{
		if (_disposed) return;

		foreach (var kvp in _processes)
		{
			var jobId = kvp.Key;
			var supervised = kvp.Value;

			if (supervised.IsCompleted) continue;

			var health = CheckProcessHealth(supervised);

			if (!health.IsHealthy)
			{
				_logger?.LogWarning("Process for job {JobId} is unhealthy: {Reason}",
					jobId, health.Reason);
				ProcessUnhealthy?.Invoke(jobId, health);
			}

			// Check for unexpected exit
			if (supervised.Process != null && supervised.Process.HasExited && !supervised.IsCompleted)
			{
				supervised.IsCompleted = true;
				supervised.ExitCode = supervised.Process.ExitCode;

				if (supervised.Process.ExitCode != 0)
				{
					_logger?.LogWarning("Process for job {JobId} exited unexpectedly with code {ExitCode}",
						jobId, supervised.Process.ExitCode);
					ProcessExitedUnexpectedly?.Invoke(jobId, supervised.Process.ExitCode);
				}
			}
		}
	}

	private ProcessHealthStatus CheckProcessHealth(SupervisedProcess supervised)
	{
		var status = new ProcessHealthStatus
		{
			JobId = supervised.JobId,
			ProcessId = supervised.ProcessId,
			Uptime = DateTime.UtcNow - supervised.StartTime,
			RestartCount = supervised.RestartCount
		};

		// Check if process has exited
		if (supervised.Process == null || supervised.Process.HasExited)
		{
			status.IsHealthy = false;
			status.Reason = "Process has exited";
			return status;
		}

		// Check memory usage
		try
		{
			supervised.Process.Refresh();
			var memoryMb = supervised.Process.WorkingSet64 / (1024 * 1024);
			status.MemoryUsageMb = memoryMb;

			if (memoryMb > MaxMemoryMb)
			{
				status.IsHealthy = false;
				status.Reason = $"Memory usage ({memoryMb} MB) exceeds limit ({MaxMemoryMb} MB)";
				return status;
			}
		}
		catch
		{
			// Can't read memory, process might be exiting
		}

		// Check for stall
		if (supervised.LastOutputTime.HasValue)
		{
			var timeSinceOutput = DateTime.UtcNow - supervised.LastOutputTime.Value;
			status.TimeSinceLastOutput = timeSinceOutput;

			if (timeSinceOutput > StallThreshold)
			{
				status.IsHealthy = false;
				status.Reason = $"No output for {timeSinceOutput} (stall threshold: {StallThreshold})";
				return status;
			}
		}
		else if (DateTime.UtcNow - supervised.StartTime > StallThreshold)
		{
			// Never received any output
			status.IsHealthy = false;
			status.Reason = "No output received since process start";
			return status;
		}

		status.IsHealthy = true;
		return status;
	}

	private async Task<bool> TerminateProcessAsync(SupervisedProcess supervised, bool graceful, TimeSpan gracefulTimeout)
	{
		if (supervised.Process == null || supervised.Process.HasExited)
		{
			return true;
		}

		try
		{
			if (graceful)
			{
				// Try graceful termination first
				supervised.CancellationTokenSource?.Cancel();

				using var timeoutCts = new CancellationTokenSource(gracefulTimeout);
				try
				{
					await supervised.Process.WaitForExitAsync(timeoutCts.Token);
					return true;
				}
				catch (OperationCanceledException)
				{
					// Graceful termination timed out, force kill
				}
			}

			// Force kill
			PlatformHelper.TryKillProcessTree(supervised.ProcessId, msg => _logger?.LogDebug("{Message}", msg));
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Error terminating process {ProcessId}", supervised.ProcessId);
			return false;
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(ProcessSupervisor));
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;

		_healthCheckTimer.Dispose();

		// Stop all processes
		foreach (var kvp in _processes)
		{
			try
			{
				kvp.Value.CancellationTokenSource?.Cancel();
				if (kvp.Value.Process != null && !kvp.Value.Process.HasExited)
				{
					PlatformHelper.TryKillProcessTree(kvp.Value.ProcessId, null);
				}
				kvp.Value.Process?.Dispose();
				kvp.Value.CancellationTokenSource?.Dispose();
			}
			catch { }
		}

		_processes.Clear();
	}
}

/// <summary>
/// Options for starting a supervised process
/// </summary>
public class ProcessStartOptions
{
	public string Executable { get; set; } = string.Empty;
	public string Arguments { get; set; } = string.Empty;
	public string? WorkingDirectory { get; set; }
	public Dictionary<string, string>? EnvironmentVariables { get; set; }
	public int MaxRestarts { get; set; } = 3;
	public TimeSpan? Timeout { get; set; }
}

/// <summary>
/// A supervised process instance
/// </summary>
public class SupervisedProcess
{
	public Guid JobId { get; set; }
	public Process? Process { get; set; }
	public int ProcessId { get; set; }
	public ProcessStartOptions Options { get; set; } = new();
	public DateTime StartTime { get; set; }
	public DateTime? LastOutputTime { get; set; }
	public DateTime? LastRestartTime { get; set; }
	public int RestartCount { get; set; }
	public bool IsCompleted { get; set; }
	public int? ExitCode { get; set; }
	public CancellationTokenSource? CancellationTokenSource { get; set; }
	public System.Text.StringBuilder OutputBuffer { get; } = new();
	public System.Text.StringBuilder ErrorBuffer { get; } = new();
	public TaskCompletionSource<bool> OutputComplete { get; } = new();
	public TaskCompletionSource<bool> ErrorComplete { get; } = new();
	public Action<string, bool>? OnOutput { get; set; }
}

/// <summary>
/// Health status for a supervised process
/// </summary>
public class ProcessHealthStatus
{
	public Guid JobId { get; set; }
	public int ProcessId { get; set; }
	public bool IsHealthy { get; set; }
	public string? Reason { get; set; }
	public TimeSpan Uptime { get; set; }
	public long MemoryUsageMb { get; set; }
	public TimeSpan? TimeSinceLastOutput { get; set; }
	public int RestartCount { get; set; }
}

/// <summary>
/// Result of process completion
/// </summary>
public class ProcessCompletionResult
{
	public bool Success { get; set; }
	public int ExitCode { get; set; }
	public TimeSpan Duration { get; set; }
	public string Output { get; set; } = string.Empty;
	public string Error { get; set; } = string.Empty;
	public bool WasCancelled { get; set; }
	public bool TimedOut { get; set; }
	public string? FailureReason { get; set; }
	public int RestartCount { get; set; }
}
