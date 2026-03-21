using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Service for generating MCP (Model Context Protocol) configurations from skills and project environments.
/// </summary>
public interface IMcpConfigService
{
/// <summary>
/// Generates a standard MCP configuration JSON string for the given project.
/// Returns null when neither skills nor Playwright MCP are needed.
/// </summary>
Task<string?> GenerateMcpConfigJsonAsync(Project? project = null, CancellationToken cancellationToken = default);

/// <summary>
/// Generates a provider-specific MCP configuration file and returns the file path.
/// Returns null when no MCP servers are required.
/// </summary>
	Task<string?> GenerateMcpConfigFileAsync(
		ProviderType providerType,
		Project? project = null,
		string? workingDirectory = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Generates provider-specific MCP execution resources, including any temporary browser artifact directories.
	/// Returns null when no MCP servers are required.
	/// </summary>
	Task<McpExecutionResources?> GenerateExecutionResourcesAsync(
		ProviderType providerType,
		Project? project = null,
		string? workingDirectory = null,
		CancellationToken cancellationToken = default);

/// <summary>
/// Gets the CLI argument for injecting MCP config based on provider type.
/// Returns null when no MCP servers are required.
/// </summary>
Task<string?> GetMcpCliArgumentAsync(
ProviderType providerType,
Project? project = null,
string? workingDirectory = null,
CancellationToken cancellationToken = default);

	/// <summary>
	/// Cleans up temporary MCP config files that are no longer needed.
	/// </summary>
	void CleanupMcpConfigFiles();

	/// <summary>
	/// Cleans up temporary resources created for a specific MCP execution.
	/// </summary>
	void CleanupExecutionResources(McpExecutionResources? resources);
}

public sealed class McpExecutionResources
{
	public string? ConfigFilePath { get; init; }

	public string? BrowserArtifactsDirectory { get; init; }
}

/// <summary>
/// Implementation of MCP configuration service that generates config for skills and Playwright MCP.
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
private const string PlaywrightServerName = "playwright";
private static readonly string[] PlaywrightCommandArgs = ["-y", "@playwright/mcp@latest"];

	private readonly ISkillService _skillService;
	private static readonly List<string> _tempConfigFiles = new();
	private static readonly List<string> _tempArtifactDirectories = new();
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

public async Task<string?> GenerateMcpConfigJsonAsync(Project? project = null, CancellationToken cancellationToken = default)
{
var skills = await _skillService.GetEnabledAsync(cancellationToken);
if (!RequiresMcp(skills, project))
{
return null;
}

	var config = BuildStandardMcpConfig(skills, project, playwrightEnvironment: null);
return JsonSerializer.Serialize(config, JsonOptions);
}

	public async Task<string?> GenerateMcpConfigFileAsync(
		ProviderType providerType,
		Project? project = null,
		string? workingDirectory = null,
		CancellationToken cancellationToken = default)
	{
		var resources = await GenerateExecutionResourcesAsync(providerType, project, workingDirectory, cancellationToken);
		return resources?.ConfigFilePath;
	}

	public async Task<McpExecutionResources?> GenerateExecutionResourcesAsync(
		ProviderType providerType,
		Project? project = null,
		string? workingDirectory = null,
		CancellationToken cancellationToken = default)
	{
		var skills = await _skillService.GetEnabledAsync(cancellationToken);
		if (!RequiresMcp(skills, project))
		{
			return null;
		}

		var browserArtifactsDirectory = HasEnabledWebEnvironment(project)
			? CreateBrowserArtifactsDirectory()
			: null;
		var filePath = CreateConfigFilePath(providerType, workingDirectory);
		var playwrightEnvironment = CreatePlaywrightEnvironmentVariables(browserArtifactsDirectory);
		var json = providerType == ProviderType.OpenCode
		? JsonSerializer.Serialize(BuildOpenCodeConfig(skills, project, playwrightEnvironment), JsonOptions)
		: JsonSerializer.Serialize(BuildStandardMcpConfig(skills, project, playwrightEnvironment), JsonOptions);

		await File.WriteAllTextAsync(filePath, json, cancellationToken);
		TrackTempFile(filePath);
		if (!string.IsNullOrWhiteSpace(browserArtifactsDirectory))
		{
			TrackTempDirectory(browserArtifactsDirectory);
		}

		return new McpExecutionResources
		{
			ConfigFilePath = filePath,
			BrowserArtifactsDirectory = browserArtifactsDirectory
		};
	}

public async Task<string?> GetMcpCliArgumentAsync(
ProviderType providerType,
Project? project = null,
string? workingDirectory = null,
CancellationToken cancellationToken = default)
{
var configFilePath = await GenerateMcpConfigFileAsync(providerType, project, workingDirectory, cancellationToken);
if (string.IsNullOrEmpty(configFilePath))
{
return null;
}

return providerType switch
{
ProviderType.Copilot => $"--additional-mcp-config \"@{configFilePath}\"",
ProviderType.Claude => $"--mcp-config \"{configFilePath}\"",
ProviderType.OpenCode => $"--config \"{configFilePath}\"",
_ => null
};
}

	public void CleanupMcpConfigFiles()
	{
		List<string> filesToClean;
		List<string> directoriesToClean;
		lock (_tempFilesLock)
		{
			filesToClean = new List<string>(_tempConfigFiles);
			_tempConfigFiles.Clear();
			directoriesToClean = new List<string>(_tempArtifactDirectories);
			_tempArtifactDirectories.Clear();
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

		foreach (var directoryPath in directoriesToClean)
		{
			TryDeleteDirectory(directoryPath);
		}
	}

	public void CleanupExecutionResources(McpExecutionResources? resources)
	{
		if (resources == null)
		{
			return;
		}

		if (!string.IsNullOrWhiteSpace(resources.ConfigFilePath))
		{
			RemoveTrackedFile(resources.ConfigFilePath);
			try
			{
				if (File.Exists(resources.ConfigFilePath))
				{
					File.Delete(resources.ConfigFilePath);
				}
			}
			catch
			{
				// Ignore cleanup errors
			}
		}

		if (!string.IsNullOrWhiteSpace(resources.BrowserArtifactsDirectory))
		{
			RemoveTrackedDirectory(resources.BrowserArtifactsDirectory);
			TryDeleteDirectory(resources.BrowserArtifactsDirectory);
		}
	}

private static bool RequiresMcp(IEnumerable<Skill> skills, Project? project)
{
return skills.Any() || HasEnabledWebEnvironment(project);
}

	private static bool HasEnabledWebEnvironment(Project? project)
	{
		return project?.Environments.Any(environment => environment.IsEnabled && environment.Type == EnvironmentType.Web) == true;
	}

	private static string CreateBrowserArtifactsDirectory()
	{
		var directoryPath = Path.Combine(Path.GetTempPath(), "vibeswarm", "browser-artifacts", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directoryPath);
		Directory.CreateDirectory(Path.Combine(directoryPath, "tmp"));
		Directory.CreateDirectory(Path.Combine(directoryPath, "ms-playwright"));
		Directory.CreateDirectory(Path.Combine(directoryPath, "cache"));
		return directoryPath;
	}

	private static Dictionary<string, string>? CreatePlaywrightEnvironmentVariables(string? browserArtifactsDirectory)
	{
		if (string.IsNullOrWhiteSpace(browserArtifactsDirectory))
		{
			return null;
		}

		var temporaryDirectory = Path.Combine(browserArtifactsDirectory, "tmp");
		return new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(browserArtifactsDirectory, "ms-playwright"),
			["TMPDIR"] = temporaryDirectory,
			["TMP"] = temporaryDirectory,
			["TEMP"] = temporaryDirectory,
			["XDG_CACHE_HOME"] = Path.Combine(browserArtifactsDirectory, "cache")
		};
	}

	private static string CreateConfigFilePath(ProviderType providerType, string? workingDirectory)
	{
		var tempDir = Path.Combine(Path.GetTempPath(), "vibeswarm", "mcp");
		Directory.CreateDirectory(tempDir);

if (providerType == ProviderType.OpenCode)
{
var fileName = $"opencode-mcp-{Guid.NewGuid():N}.json";
	return Path.Combine(tempDir, fileName);
}

var standardFileName = $"mcp-config-{Guid.NewGuid():N}.json";
return Path.Combine(tempDir, standardFileName);
}

	private static void TrackTempFile(string filePath)
	{
		lock (_tempFilesLock)
		{
			_tempConfigFiles.Add(filePath);
		}
	}

	private static void TrackTempDirectory(string directoryPath)
	{
		lock (_tempFilesLock)
		{
			_tempArtifactDirectories.Add(directoryPath);
		}
	}

	private static void RemoveTrackedFile(string filePath)
	{
		lock (_tempFilesLock)
		{
			_tempConfigFiles.Remove(filePath);
		}
	}

	private static void RemoveTrackedDirectory(string directoryPath)
	{
		lock (_tempFilesLock)
		{
			_tempArtifactDirectories.Remove(directoryPath);
		}
	}

	private static void TryDeleteDirectory(string directoryPath)
	{
		try
		{
			if (Directory.Exists(directoryPath))
			{
				Directory.Delete(directoryPath, recursive: true);
			}
		}
		catch
		{
			// Ignore cleanup errors
		}
	}

	private static McpConfig BuildStandardMcpConfig(
		IEnumerable<Skill> skills,
		Project? project,
		Dictionary<string, string>? playwrightEnvironment)
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
Args = [skill.Name],
Env = new Dictionary<string, string>
{
["VIBESWARM_SKILL_CONTENT"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(skill.Content))
}
};
}

if (HasEnabledWebEnvironment(project))
{
var serverName = GetUniqueServerName(config.McpServers.Keys, PlaywrightServerName);
		config.McpServers[serverName] = new McpServer
		{
			Command = "npx",
			Args = PlaywrightCommandArgs,
			Env = playwrightEnvironment
		};
	}

return config;
}

	private static OpenCodeMcpConfig BuildOpenCodeConfig(
		IEnumerable<Skill> skills,
		Project? project,
		Dictionary<string, string>? playwrightEnvironment)
	{
		var config = new OpenCodeMcpConfig();

foreach (var skill in skills)
{
var serverName = SanitizeSkillName(skill.Name);
config.Mcp[serverName] = new OpenCodeMcpServer
{
Type = "local",
Command = ["vibeswarm-skill", skill.Name],
Enabled = true,
Environment = new Dictionary<string, string>
{
["VIBESWARM_SKILL_CONTENT"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(skill.Content))
}
};
}

if (HasEnabledWebEnvironment(project))
{
var serverName = GetUniqueServerName(config.Mcp.Keys, PlaywrightServerName);
		config.Mcp[serverName] = new OpenCodeMcpServer
		{
			Type = "local",
			Command = ["npx", ..PlaywrightCommandArgs],
			Enabled = true,
			Environment = playwrightEnvironment
		};
	}

return config;
}

private static string GetUniqueServerName(IEnumerable<string> existingNames, string preferredName)
{
var usedNames = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
if (!usedNames.Contains(preferredName))
{
return preferredName;
}

var suffix = 1;
while (usedNames.Contains($"{preferredName}-{suffix}"))
{
suffix++;
}

return $"{preferredName}-{suffix}";
}

private static string SanitizeSkillName(string name)
{
return System.Text.RegularExpressions.Regex.Replace(
name.ToLowerInvariant(),
@"[^a-z0-9\-_]",
"-");
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
