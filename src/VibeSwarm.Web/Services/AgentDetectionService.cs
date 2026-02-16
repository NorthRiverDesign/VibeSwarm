using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Scans for installed CLI coding agents at startup and auto-registers them as providers.
/// </summary>
public class AgentDetectionService
{
	private readonly VibeSwarmDbContext _db;
	private readonly ILogger<AgentDetectionService> _logger;

	/// <summary>
	/// Known CLI agents with their executable names and provider types.
	/// </summary>
	private static readonly (string Name, string Executable, ProviderType Type)[] KnownAgents =
	[
		("Claude Code", "claude", ProviderType.Claude),
		("OpenCode", "opencode", ProviderType.OpenCode),
		("GitHub Copilot", "copilot", ProviderType.Copilot),
	];

	private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(10);

	public AgentDetectionService(VibeSwarmDbContext db, ILogger<AgentDetectionService> logger)
	{
		_db = db;
		_logger = logger;
	}

	/// <summary>
	/// Detects installed CLI agents and registers any that are not already in the database.
	/// </summary>
	public async Task DetectAndRegisterAsync(CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Scanning for installed CLI coding agents...");

		var detected = new List<string>();
		var missing = new List<string>();
		var hasDefault = await _db.Providers.AnyAsync(p => p.IsDefault, cancellationToken);

		foreach (var (name, executable, providerType) in KnownAgents)
		{
			var resolvedPath = PlatformHelper.ResolveExecutablePath(executable);

			// If it resolved to the bare name, the executable wasn't found on PATH
			if (resolvedPath == executable && !File.Exists(resolvedPath))
			{
				missing.Add(name);
				_logger.LogDebug("{Agent} not found on PATH (looked for '{Executable}')", name, executable);
				continue;
			}

			// Verify the executable actually works by running --version
			var version = await GetVersionAsync(resolvedPath, cancellationToken);
			if (version is null)
			{
				missing.Add(name);
				_logger.LogWarning("{Agent} found at {Path} but failed version check", name, resolvedPath);
				continue;
			}

			// Check if this provider type is already registered
			var exists = await _db.Providers.AnyAsync(p => p.Type == providerType, cancellationToken);
			if (exists)
			{
				detected.Add(name);
				_logger.LogDebug("{Agent} already registered, skipping", name);
				continue;
			}

			// Auto-register the provider
			var provider = new Provider
			{
				Name = name,
				Type = providerType,
				ConnectionMode = ProviderConnectionMode.CLI,
				ExecutablePath = resolvedPath,
				IsEnabled = true,
				IsDefault = !hasDefault, // first detected becomes default
			};

			_db.Providers.Add(provider);
			detected.Add(name);
			hasDefault = true; // subsequent agents won't become default

			_logger.LogInformation("Auto-registered {Agent} (v{Version}) at {Path}", name, version.Trim(), resolvedPath);
		}

		if (_db.ChangeTracker.HasChanges())
		{
			await _db.SaveChangesAsync(cancellationToken);
		}

		_logger.LogInformation(
			"Agent detection complete. Detected: [{Detected}]. Not found: [{Missing}]",
			detected.Count > 0 ? string.Join(", ", detected) : "none",
			missing.Count > 0 ? string.Join(", ", missing) : "none");
	}

	/// <summary>
	/// Runs --version on the executable and returns the output, or null on failure.
	/// </summary>
	private async Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken)
	{
		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = executablePath,
				Arguments = "--version",
			};

			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var process = new Process { StartInfo = startInfo };
			using var timeoutCts = new CancellationTokenSource(VersionCheckTimeout);
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			process.Start();

			try { process.StandardInput.Close(); }
			catch { /* stdin may already be closed */ }

			try
			{
				await process.WaitForExitAsync(linkedCts.Token);
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
			{
				try { process.Kill(entireProcessTree: true); } catch { }
				return null;
			}

			var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
			return process.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to run --version for {Executable}", executablePath);
			return null;
		}
	}
}
