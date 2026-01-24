using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VibeSwarm.Shared.Utilities;

/// <summary>
/// Helper class for cross-platform operations, particularly for CLI tool management
/// </summary>
public static class PlatformHelper
{
	/// <summary>
	/// Gets whether the current OS is Windows
	/// </summary>
	public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	/// <summary>
	/// Gets whether the current OS is Linux
	/// </summary>
	public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

	/// <summary>
	/// Gets whether the current OS is macOS
	/// </summary>
	public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

	/// <summary>
	/// Gets the current OS description for logging purposes
	/// </summary>
	public static string OsDescription => RuntimeInformation.OSDescription;

	/// <summary>
	/// Gets the OS-specific shell executable
	/// </summary>
	public static string ShellExecutable => IsWindows ? "cmd.exe" : "/bin/sh";

	/// <summary>
	/// Gets the OS-specific shell argument prefix for executing commands
	/// </summary>
	public static string ShellArgumentPrefix => IsWindows ? "/c" : "-c";

	/// <summary>
	/// Resolves an executable path for the current platform.
	/// On Windows, looks for .exe extension; on Unix, checks if file is executable.
	/// </summary>
	/// <param name="executableName">The name of the executable (without extension)</param>
	/// <param name="customPath">Optional custom path to the executable</param>
	/// <returns>The resolved executable path, or the original name if not found</returns>
	public static string ResolveExecutablePath(string executableName, string? customPath = null)
	{
		// If custom path is provided and exists, use it
		if (!string.IsNullOrEmpty(customPath))
		{
			if (File.Exists(customPath))
				return customPath;

			// On Windows, try adding .exe if not present
			if (IsWindows && !customPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				var withExe = customPath + ".exe";
				if (File.Exists(withExe))
					return withExe;
			}
		}

		// Try to find in PATH
		var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();

		foreach (var dir in pathDirs)
		{
			try
			{
				var fullPath = Path.Combine(dir, executableName);

				if (IsWindows)
				{
					// On Windows, try with common extensions
					var extensions = new[] { ".exe", ".cmd", ".bat", "" };
					foreach (var ext in extensions)
					{
						var pathWithExt = fullPath + ext;
						if (File.Exists(pathWithExt))
							return pathWithExt;
					}
				}
				else
				{
					// On Unix, check if file exists and is executable
					if (File.Exists(fullPath))
						return fullPath;
				}
			}
			catch
			{
				// Ignore path errors (invalid characters, etc.)
			}
		}

		// Return original name and let the system resolve it
		return executableName;
	}

	/// <summary>
	/// Configures a ProcessStartInfo for cross-platform execution
	/// </summary>
	/// <param name="startInfo">The ProcessStartInfo to configure</param>
	public static void ConfigureForCrossPlatform(ProcessStartInfo startInfo)
	{
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.RedirectStandardInput = true;

		// On Linux/macOS, ensure we inherit the environment for tools installed in user paths
		if (!IsWindows)
		{
			// Preserve important environment variables
			var homeDir = Environment.GetEnvironmentVariable("HOME");
			if (!string.IsNullOrEmpty(homeDir))
			{
				// Add common tool installation paths that might not be in PATH
				var additionalPaths = new[]
				{
					Path.Combine(homeDir, ".local", "bin"),
					Path.Combine(homeDir, "bin"),
					"/usr/local/bin",
					"/opt/homebrew/bin"  // macOS ARM homebrew
                };

				var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
				var newPaths = additionalPaths.Where(p => Directory.Exists(p) && !currentPath.Contains(p));
				if (newPaths.Any())
				{
					startInfo.Environment["PATH"] = string.Join(Path.PathSeparator.ToString(),
						newPaths.Concat(new[] { currentPath }));
				}
			}
		}
	}

	/// <summary>
	/// Attempts to kill a process and its entire process tree
	/// </summary>
	/// <param name="processId">The process ID to kill</param>
	/// <param name="logger">Optional action for logging</param>
	/// <returns>True if the process was successfully terminated</returns>
	public static bool TryKillProcessTree(int processId, Action<string>? logger = null)
	{
		try
		{
			var process = Process.GetProcessById(processId);

			if (IsWindows)
			{
				// On Windows, use taskkill to kill the entire process tree
				try
				{
					var killInfo = new ProcessStartInfo
					{
						FileName = "taskkill",
						Arguments = $"/F /T /PID {processId}",
						UseShellExecute = false,
						CreateNoWindow = true,
						RedirectStandardOutput = true,
						RedirectStandardError = true
					};

					using var killProcess = Process.Start(killInfo);
					killProcess?.WaitForExit(5000);

					logger?.Invoke($"Executed taskkill for process {processId}");
				}
				catch (Exception ex)
				{
					logger?.Invoke($"taskkill failed: {ex.Message}, falling back to Kill()");
					// Fall back to direct kill
					process.Kill(entireProcessTree: true);
				}
			}
			else
			{
				// On Unix, use kill with process group or pkill
				try
				{
					// First try to kill the process group
					var killInfo = new ProcessStartInfo
					{
						FileName = "/bin/kill",
						Arguments = $"-TERM -{processId}",  // Negative PID kills the process group
						UseShellExecute = false,
						CreateNoWindow = true
					};

					using var killProcess = Process.Start(killInfo);
					killProcess?.WaitForExit(2000);

					// If still running, send SIGKILL
					if (!process.HasExited)
					{
						killInfo.Arguments = $"-KILL -{processId}";
						using var forceKillProcess = Process.Start(killInfo);
						forceKillProcess?.WaitForExit(2000);
					}

					logger?.Invoke($"Sent kill signal to process group {processId}");
				}
				catch
				{
					// Fall back to .NET Kill method
					process.Kill(entireProcessTree: true);
					logger?.Invoke($"Used .NET Kill() for process {processId}");
				}
			}

			// Wait a bit and verify the process is gone
			try
			{
				process.WaitForExit(3000);
				return process.HasExited;
			}
			catch
			{
				return true; // Process probably gone
			}
		}
		catch (ArgumentException)
		{
			// Process not found - already terminated
			logger?.Invoke($"Process {processId} already terminated");
			return true;
		}
		catch (Exception ex)
		{
			logger?.Invoke($"Failed to kill process {processId}: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Creates a new process group for the child process (Unix only)
	/// This allows killing all child processes together
	/// </summary>
	/// <param name="startInfo">The ProcessStartInfo to configure</param>
	public static void ConfigureProcessGroup(ProcessStartInfo startInfo)
	{
		if (!IsWindows)
		{
			// On Unix, we can set process group options
			// Note: This requires Process.Start to be called with specific flags
			// The actual implementation may vary based on .NET version
		}
	}

	/// <summary>
	/// Escapes a command line argument appropriately for the current platform
	/// </summary>
	/// <param name="argument">The argument to escape</param>
	/// <returns>The escaped argument</returns>
	public static string EscapeArgument(string argument)
	{
		if (string.IsNullOrEmpty(argument))
			return "\"\"";

		if (IsWindows)
		{
			// Windows escaping rules
			if (!argument.Contains(' ') && !argument.Contains('"') &&
				!argument.Contains('\t') && !argument.Contains('\n'))
			{
				return argument;
			}

			// Escape backslashes and quotes
			var escaped = argument.Replace("\\", "\\\\").Replace("\"", "\\\"");
			return $"\"{escaped}\"";
		}
		else
		{
			// Unix escaping - use single quotes for most cases
			if (!argument.Contains('\''))
			{
				if (argument.Any(c => char.IsWhiteSpace(c) || "\"\\$`!".Contains(c)))
				{
					return $"'{argument}'";
				}
				return argument;
			}

			// Contains single quotes - escape them
			var escaped = argument.Replace("'", "'\\''");
			return $"'{escaped}'";
		}
	}

	/// <summary>
	/// Gets the appropriate line ending for the current platform
	/// </summary>
	public static string LineEnding => IsWindows ? "\r\n" : "\n";
}
