using System.Diagnostics;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared;

namespace VibeSwarm.Web.Services;

public sealed class ProviderCliDetectionService(ILogger<ProviderCliDetectionService> logger)
{
	private readonly ILogger<ProviderCliDetectionService> _logger = logger;
	private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(10);

	public async Task<ProviderCliDetectionResult> DetectAsync(
		ProviderType providerType,
		string executableName,
		string versionArguments,
		string? configuredExecutablePath = null,
		string? homeDirectory = null,
		string? searchPath = null,
		CancellationToken cancellationToken = default)
	{
		homeDirectory ??= GetHomeDirectory();
		searchPath ??= PlatformHelper.GetEnhancedPath(homeDirectory);
		var candidates = BuildCandidates(executableName, configuredExecutablePath, searchPath);

		foreach (var candidate in candidates)
		{
			var result = await ProbeCandidateAsync(candidate.Path, versionArguments, homeDirectory, searchPath, cancellationToken);
			if (result.IsInstalled)
			{
				return result with
				{
					Message = $"Detected on host as {providerType} CLI."
				};
			}

			if (!candidate.Required)
			{
				continue;
			}

			_logger.LogDebug(
				"{ProviderType} configured executable path {ExecutablePath} failed detection: {Message}",
				providerType,
				candidate.Path,
				result.Message);
		}

		var configuredPath = configuredExecutablePath?.Trim();
		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			return new ProviderCliDetectionResult(
				IsInstalled: false,
				Version: null,
				ResolvedExecutablePath: null,
				Message: $"Configured path '{configuredPath}' was not runnable. {AppConstants.AppName} also could not find '{executableName}' on the host PATH.");
		}

		return new ProviderCliDetectionResult(
			IsInstalled: false,
			Version: null,
			ResolvedExecutablePath: null,
			Message: $"{AppConstants.AppName} could not find '{executableName}' on the host PATH. It also checked common user install locations.");
	}

	public async Task<ProviderCliDetectionResult> DetectAnyAsync(
		IReadOnlyList<string> executableNames,
		string versionArguments,
		string? configuredExecutablePath = null,
		string? homeDirectory = null,
		string? searchPath = null,
		CancellationToken cancellationToken = default)
	{
		if (executableNames == null || executableNames.Count == 0)
		{
			throw new ArgumentException("At least one executable name is required.", nameof(executableNames));
		}

		homeDirectory ??= GetHomeDirectory();
		searchPath ??= PlatformHelper.GetEnhancedPath(homeDirectory);
		var candidates = BuildCandidates(executableNames, configuredExecutablePath, searchPath);

		foreach (var candidate in candidates)
		{
			var result = await ProbeCandidateAsync(candidate.Path, versionArguments, homeDirectory, searchPath, cancellationToken);
			if (result.IsInstalled)
			{
				return result;
			}

			if (!candidate.Required)
			{
				continue;
			}

			_logger.LogDebug(
				"Configured executable path {ExecutablePath} failed detection: {Message}",
				candidate.Path,
				result.Message);
		}

		var configuredPath = configuredExecutablePath?.Trim();
		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			return new ProviderCliDetectionResult(
				IsInstalled: false,
				Version: null,
				ResolvedExecutablePath: null,
				Message: $"Configured path '{configuredPath}' was not runnable. {AppConstants.AppName} also could not find any of '{string.Join("', '", executableNames)}' on the host PATH.");
		}

		return new ProviderCliDetectionResult(
			IsInstalled: false,
			Version: null,
			ResolvedExecutablePath: null,
			Message: $"{AppConstants.AppName} could not find any of '{string.Join("', '", executableNames)}' on the host PATH. It also checked common user install locations.");
	}

	private async Task<ProviderCliDetectionResult> ProbeCandidateAsync(
		string executablePath,
		string versionArguments,
		string? homeDirectory,
		string searchPath,
		CancellationToken cancellationToken)
	{
		try
		{
			using var timeoutCts = new CancellationTokenSource(DetectionTimeout);
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			var startInfo = new ProcessStartInfo
			{
				FileName = executablePath,
				Arguments = versionArguments
			};

			PlatformHelper.ConfigureForCrossPlatform(startInfo, homeDirectory, searchPath);

			using var process = new Process { StartInfo = startInfo };
			process.Start();

			try
			{
				process.StandardInput.Close();
			}
			catch
			{
				// Some CLIs do not expose stdin during version checks.
			}

			var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
			var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

			try
			{
				await process.WaitForExitAsync(linkedCts.Token);
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch
				{
					// Best-effort cleanup only.
				}

				return new ProviderCliDetectionResult(
					IsInstalled: false,
					Version: null,
					ResolvedExecutablePath: executablePath,
					Message: $"Version detection timed out while running '{executablePath} {versionArguments}'.");
			}

			var output = await outputTask;
			var error = await errorTask;
			var version = NormalizeVersion(output) ?? NormalizeVersion(error);

			if (process.ExitCode == 0)
			{
				return new ProviderCliDetectionResult(
					IsInstalled: true,
					Version: version,
					ResolvedExecutablePath: executablePath,
					Message: "Detected on host.");
			}

			var failureMessage = NormalizeVersion(error) ??
				NormalizeVersion(output) ??
				$"The command exited with code {process.ExitCode}.";

			return new ProviderCliDetectionResult(
				IsInstalled: false,
				Version: null,
				ResolvedExecutablePath: executablePath,
				Message: $"Found '{executablePath}', but '{versionArguments}' failed: {failureMessage}");
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to probe executable {ExecutablePath}", executablePath);
			return new ProviderCliDetectionResult(
				IsInstalled: false,
				Version: null,
				ResolvedExecutablePath: executablePath,
				Message: $"Failed to start '{executablePath}': {ex.Message}");
		}
	}

	private static List<DetectionCandidate> BuildCandidates(string executableName, string? configuredExecutablePath, string searchPath)
	{
		return BuildCandidates([executableName], configuredExecutablePath, searchPath);
	}

	private static List<DetectionCandidate> BuildCandidates(IReadOnlyList<string> executableNames, string? configuredExecutablePath, string searchPath)
	{
		var candidates = new List<DetectionCandidate>();
		var comparer = PlatformHelper.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

		var resolvedConfiguredPath = ResolveConfiguredExecutablePath(configuredExecutablePath, searchPath);
		if (!string.IsNullOrWhiteSpace(resolvedConfiguredPath))
		{
			candidates.Add(new DetectionCandidate(resolvedConfiguredPath, Required: true));
		}

		foreach (var executableName in executableNames)
		{
			var resolvedDefaultPath = PlatformHelper.ResolveExecutablePath(executableName, searchPath: searchPath);
			if (resolvedDefaultPath == executableName && !File.Exists(resolvedDefaultPath))
			{
				continue;
			}

			if (!candidates.Any(candidate => comparer.Equals(candidate.Path, resolvedDefaultPath)))
			{
				candidates.Add(new DetectionCandidate(resolvedDefaultPath, Required: false));
			}
		}

		return candidates;
	}

	private static string? ResolveConfiguredExecutablePath(string? configuredExecutablePath, string searchPath)
	{
		if (string.IsNullOrWhiteSpace(configuredExecutablePath))
		{
			return null;
		}

		var trimmedPath = configuredExecutablePath.Trim();
		if (File.Exists(trimmedPath))
		{
			return trimmedPath;
		}

		if (PlatformHelper.IsWindows && !trimmedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			var pathWithExtension = trimmedPath + ".exe";
			if (File.Exists(pathWithExtension))
			{
				return pathWithExtension;
			}
		}

		if (Path.IsPathRooted(trimmedPath) ||
			trimmedPath.Contains(Path.DirectorySeparatorChar) ||
			trimmedPath.Contains(Path.AltDirectorySeparatorChar))
		{
			return null;
		}

		var resolvedPath = PlatformHelper.ResolveExecutablePath(trimmedPath, searchPath: searchPath);
		return resolvedPath == trimmedPath && !File.Exists(resolvedPath) ? null : resolvedPath;
	}

	private static string? GetHomeDirectory()
	{
		var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return string.IsNullOrWhiteSpace(homeDirectory) ? Environment.GetEnvironmentVariable("HOME") : homeDirectory;
	}

	private static string? NormalizeVersion(string? output)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return null;
		}

		return output
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault();
	}

	private sealed record DetectionCandidate(string Path, bool Required);
}

public sealed record ProviderCliDetectionResult(
	bool IsInstalled,
	string? Version,
	string? ResolvedExecutablePath,
	string Message);
