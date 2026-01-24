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
	/// Gets an enhanced PATH that includes common CLI tool installation directories.
	/// This is essential for systemd services which run with minimal environments.
	/// </summary>
	/// <param name="homeDir">The user's home directory (if null, will try to detect)</param>
	/// <returns>A PATH string with additional common tool directories</returns>
	public static string GetEnhancedPath(string? homeDir = null)
	{
		var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";

		if (IsWindows)
		{
			return currentPath;
		}

		// Try to determine home directory if not provided
		if (string.IsNullOrEmpty(homeDir))
		{
			homeDir = Environment.GetEnvironmentVariable("HOME");
		}

		var additionalPaths = new List<string>();

		// Always add system paths that might be missing in systemd services
		additionalPaths.AddRange(new[]
		{
			"/usr/local/bin",
			"/usr/local/sbin",
			"/usr/bin",
			"/usr/sbin",
			"/bin",
			"/sbin",
			"/snap/bin",  // Ubuntu snap packages
			"/opt/homebrew/bin",  // macOS ARM homebrew
			"/opt/homebrew/sbin"
		});

		if (!string.IsNullOrEmpty(homeDir))
		{
			// User-specific paths where CLI tools are commonly installed
			additionalPaths.AddRange(new[]
			{
				Path.Combine(homeDir, ".local", "bin"),  // pip, pipx, many tools
				Path.Combine(homeDir, "bin"),
				Path.Combine(homeDir, ".cargo", "bin"),  // Rust/Cargo tools
				Path.Combine(homeDir, "go", "bin"),  // Go tools
				Path.Combine(homeDir, ".npm-global", "bin"),  // npm global (custom prefix)
				Path.Combine(homeDir, ".npm-packages", "bin"),  // npm global (another common location)
			});

			// nvm (Node Version Manager) - check for installed versions
			var nvmDir = Path.Combine(homeDir, ".nvm", "versions", "node");
			if (Directory.Exists(nvmDir))
			{
				try
				{
					// Add the most recent node version's bin directory
					var nodeVersions = Directory.GetDirectories(nvmDir)
						.OrderByDescending(d => d)
						.Take(3);  // Add the 3 most recent versions

					foreach (var versionDir in nodeVersions)
					{
						additionalPaths.Add(Path.Combine(versionDir, "bin"));
					}
				}
				catch
				{
					// Ignore errors scanning nvm directory
				}
			}

			// fnm (Fast Node Manager) - similar to nvm
			var fnmDir = Path.Combine(homeDir, ".local", "share", "fnm", "node-versions");
			if (Directory.Exists(fnmDir))
			{
				try
				{
					var nodeVersions = Directory.GetDirectories(fnmDir)
						.OrderByDescending(d => d)
						.Take(3);

					foreach (var versionDir in nodeVersions)
					{
						additionalPaths.Add(Path.Combine(versionDir, "installation", "bin"));
					}
				}
				catch
				{
					// Ignore errors scanning fnm directory
				}
			}

			// volta (Node version manager)
			var voltaBin = Path.Combine(homeDir, ".volta", "bin");
			if (Directory.Exists(voltaBin))
			{
				additionalPaths.Add(voltaBin);
			}

			// pyenv
			var pyenvShims = Path.Combine(homeDir, ".pyenv", "shims");
			if (Directory.Exists(pyenvShims))
			{
				additionalPaths.Add(pyenvShims);
			}

			// rbenv (Ruby)
			var rbenvShims = Path.Combine(homeDir, ".rbenv", "shims");
			if (Directory.Exists(rbenvShims))
			{
				additionalPaths.Add(rbenvShims);
			}

			// Deno
			var denoBin = Path.Combine(homeDir, ".deno", "bin");
			if (Directory.Exists(denoBin))
			{
				additionalPaths.Add(denoBin);
			}

			// Bun
			var bunBin = Path.Combine(homeDir, ".bun", "bin");
			if (Directory.Exists(bunBin))
			{
				additionalPaths.Add(bunBin);
			}
		}

		// Filter to only existing paths not already in PATH
		var newPaths = additionalPaths
			.Where(p => Directory.Exists(p) && !currentPath.Contains(p))
			.Distinct();

		if (newPaths.Any())
		{
			return string.Join(Path.PathSeparator.ToString(),
				newPaths.Concat(new[] { currentPath }));
		}

		return currentPath;
	}

	/// <summary>
	/// Configures a ProcessStartInfo for cross-platform execution.
	/// This method ensures that CLI tools installed in common locations can be found,
	/// which is especially important when running as a systemd service.
	/// </summary>
	/// <param name="startInfo">The ProcessStartInfo to configure</param>
	/// <param name="homeDir">Optional user home directory to use for path resolution</param>
	public static void ConfigureForCrossPlatform(ProcessStartInfo startInfo, string? homeDir = null)
	{
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.RedirectStandardInput = true;

		// On Linux/macOS, ensure we have comprehensive PATH for CLI tools
		if (!IsWindows)
		{
			startInfo.Environment["PATH"] = GetEnhancedPath(homeDir);

			// Also ensure HOME is set (may be missing in systemd services)
			if (!string.IsNullOrEmpty(homeDir) && string.IsNullOrEmpty(startInfo.Environment["HOME"]))
			{
				startInfo.Environment["HOME"] = homeDir;
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
