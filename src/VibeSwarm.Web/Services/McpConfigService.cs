using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Service for generating MCP (Model Context Protocol) configurations from skills.
/// </summary>
public interface IMcpConfigService
{
	/// <summary>
	/// Generates an MCP configuration JSON string for the given skills.
	/// </summary>
	Task<string> GenerateMcpConfigJsonAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Generates an MCP configuration file and returns the file path.
	/// The file is created in a temporary directory and can be passed to CLI tools.
	/// </summary>
	Task<string> GenerateMcpConfigFileAsync(string? workingDirectory = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the CLI argument for injecting MCP config based on provider type.
	/// </summary>
	Task<string?> GetMcpCliArgumentAsync(ProviderType providerType, string? workingDirectory = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Cleans up temporary MCP config files that are no longer needed.
	/// </summary>
	void CleanupMcpConfigFiles();
}

/// <summary>
/// Implementation of MCP configuration service that generates config for skills.
/// </summary>
/// <remarks>
/// Note: This service does NOT implement IDisposable because MCP config files must persist
/// beyond the scope lifetime. The generated config files are used by CLI tools that run
/// after this service's scope has ended. Cleanup is performed by CleanupMcpConfigFiles()
/// which should be called explicitly when jobs complete, or files will be cleaned up
/// on the next application start or by the OS temp file cleanup.
/// </remarks>
public class McpConfigService : IMcpConfigService
{
	private readonly ISkillService _skillService;
	private static readonly List<string> _tempConfigFiles = new();
	private static readonly object _tempFilesLock = new();
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public McpConfigService(ISkillService skillService)
	{
		_skillService = skillService;
	}

	public async Task<string> GenerateMcpConfigJsonAsync(CancellationToken cancellationToken = default)
	{
		var skills = await _skillService.GetEnabledAsync(cancellationToken);
		var config = BuildMcpConfig(skills);
		return JsonSerializer.Serialize(config, JsonOptions);
	}

	public async Task<string> GenerateMcpConfigFileAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
	{
		var json = await GenerateMcpConfigJsonAsync(cancellationToken);

		// Create temp directory for MCP configs if it doesn't exist
		var tempDir = Path.Combine(Path.GetTempPath(), "vibeswarm", "mcp");
		Directory.CreateDirectory(tempDir);

		// Generate unique filename
		var fileName = $"mcp-config-{Guid.NewGuid():N}.json";
		var filePath = Path.Combine(tempDir, fileName);

		await File.WriteAllTextAsync(filePath, json, cancellationToken);

		lock (_tempFilesLock)
		{
			_tempConfigFiles.Add(filePath);
		}
		return filePath;
	}

	public async Task<string?> GetMcpCliArgumentAsync(ProviderType providerType, string? workingDirectory = null, CancellationToken cancellationToken = default)
	{
		var enabledSkills = await _skillService.GetEnabledAsync(cancellationToken);
		if (!enabledSkills.Any())
		{
			return null;
		}

		var configFilePath = await GenerateMcpConfigFileAsync(workingDirectory, cancellationToken);

		return providerType switch
		{
			// GitHub Copilot CLI: --additional-mcp-config @filepath
			ProviderType.Copilot => $"--additional-mcp-config \"@{configFilePath}\"",

			// Claude Code: --mcp-config filepath
			ProviderType.Claude => $"--mcp-config \"{configFilePath}\"",

			// OpenCode uses a different config approach - config file in project directory
			// For OpenCode, we'll generate an opencode.json file
			ProviderType.OpenCode => await GenerateOpenCodeConfigAsync(workingDirectory, cancellationToken),

			_ => null
		};
	}

	private async Task<string?> GenerateOpenCodeConfigAsync(string? workingDirectory, CancellationToken cancellationToken)
	{
		var skills = await _skillService.GetEnabledAsync(cancellationToken);
		if (!skills.Any())
		{
			return null;
		}

		// OpenCode expects config in the working directory as opencode.json
		// We'll create a vibeswarm-mcp.json that can be referenced
		var configDir = workingDirectory ?? Path.GetTempPath();
		var configPath = Path.Combine(configDir, ".vibeswarm-mcp.json");

		var openCodeConfig = new OpenCodeMcpConfig
		{
			Schema = "https://opencode.ai/config.json",
			Mcp = skills.ToDictionary(
				s => SanitizeSkillName(s.Name),
				s => new OpenCodeMcpServer
				{
					Type = "local",
					Command = new[] { "vibeswarm-skill", s.Name },
					Enabled = true
				}
			)
		};

		var json = JsonSerializer.Serialize(openCodeConfig, JsonOptions);
		await File.WriteAllTextAsync(configPath, json, cancellationToken);

		lock (_tempFilesLock)
		{
			_tempConfigFiles.Add(configPath);
		}

		// Return the CLI argument format for OpenCode
		return $"--config \"{configPath}\"";
	}

	private static McpConfig BuildMcpConfig(IEnumerable<Skill> skills)
	{
		var config = new McpConfig
		{
			McpServers = new Dictionary<string, McpServer>()
		};

		foreach (var skill in skills)
		{
			var serverName = SanitizeSkillName(skill.Name);

			config.McpServers[serverName] = new McpServer
			{
				Command = "vibeswarm-skill",
				Args = new[] { skill.Name },
				Env = new Dictionary<string, string>
				{
					["VIBESWARM_SKILL_CONTENT"] = Convert.ToBase64String(
						System.Text.Encoding.UTF8.GetBytes(skill.Content))
				}
			};
		}

		return config;
	}

	private static string SanitizeSkillName(string name)
	{
		// Convert to lowercase and replace invalid characters with hyphens
		return System.Text.RegularExpressions.Regex.Replace(
			name.ToLowerInvariant(),
			@"[^a-z0-9\-_]",
			"-");
	}

	public void CleanupMcpConfigFiles()
	{
		List<string> filesToClean;
		lock (_tempFilesLock)
		{
			filesToClean = new List<string>(_tempConfigFiles);
			_tempConfigFiles.Clear();
		}

		foreach (var filePath in filesToClean)
		{
			try
			{
				if (File.Exists(filePath))
				{
					File.Delete(filePath);
				}
			}
			catch
			{
				// Ignore cleanup errors
			}
		}
	}
}

/// <summary>
/// MCP configuration format for Copilot and Claude
/// </summary>
public class McpConfig
{
	[JsonPropertyName("mcpServers")]
	public Dictionary<string, McpServer> McpServers { get; set; } = new();
}

public class McpServer
{
	[JsonPropertyName("command")]
	public string Command { get; set; } = string.Empty;

	[JsonPropertyName("args")]
	public string[]? Args { get; set; }

	[JsonPropertyName("env")]
	public Dictionary<string, string>? Env { get; set; }
}

/// <summary>
/// OpenCode specific configuration format
/// </summary>
public class OpenCodeMcpConfig
{
	[JsonPropertyName("$schema")]
	public string Schema { get; set; } = "https://opencode.ai/config.json";

	[JsonPropertyName("mcp")]
	public Dictionary<string, OpenCodeMcpServer> Mcp { get; set; } = new();
}

public class OpenCodeMcpServer
{
	[JsonPropertyName("type")]
	public string Type { get; set; } = "local";

	[JsonPropertyName("command")]
	public string[]? Command { get; set; }

	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = true;

	[JsonPropertyName("environment")]
	public Dictionary<string, string>? Environment { get; set; }
}
