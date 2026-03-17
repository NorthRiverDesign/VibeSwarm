using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Scans for installed CLI coding agents at startup and auto-registers them as providers.
/// </summary>
public class AgentDetectionService
{
	private readonly VibeSwarmDbContext _db;
	private readonly ILogger<AgentDetectionService> _logger;
	private readonly ProviderCliDetectionService _providerCliDetectionService;

	/// <summary>
	/// Known CLI agents with their executable names and provider types.
	/// </summary>
	private static readonly (string Name, string Executable, string VersionArguments, ProviderType Type)[] KnownAgents =
	[
		("Claude Code", "claude", "--version", ProviderType.Claude),
		("OpenCode", "opencode", "--version", ProviderType.OpenCode),
		("GitHub Copilot", "copilot", "--binary-version", ProviderType.Copilot),
	];

	public AgentDetectionService(
		VibeSwarmDbContext db,
		ILogger<AgentDetectionService> logger,
		ProviderCliDetectionService providerCliDetectionService)
	{
		_db = db;
		_logger = logger;
		_providerCliDetectionService = providerCliDetectionService;
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

		foreach (var (name, executable, versionArguments, providerType) in KnownAgents)
		{
			var detectionResult = await _providerCliDetectionService.DetectAsync(
				providerType,
				executable,
				versionArguments,
				cancellationToken: cancellationToken);

			if (!detectionResult.IsInstalled)
			{
				missing.Add(name);
				_logger.LogDebug("{Agent} was not detected: {Message}", name, detectionResult.Message);
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
				ExecutablePath = detectionResult.ResolvedExecutablePath ?? executable,
				IsEnabled = true,
				IsDefault = !hasDefault, // first detected becomes default
			};

			_db.Providers.Add(provider);
			detected.Add(name);
			hasDefault = true; // subsequent agents won't become default

			_logger.LogInformation(
				"Auto-registered {Agent} ({Version}) at {Path}",
				name,
				string.IsNullOrWhiteSpace(detectionResult.Version) ? "version unknown" : $"v{detectionResult.Version}",
				detectionResult.ResolvedExecutablePath ?? executable);
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
}
