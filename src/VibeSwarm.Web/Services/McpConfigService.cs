using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;

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
	/// Generates provider-specific execution resources, including any temporary MCP files, browser artifact directories,
	/// and shell environment files needed by the target provider.
	/// Returns null when no provider-specific execution resources are required.
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

	public string? BashEnvFilePath { get; init; }
}

/// <summary>
/// Implementation of MCP configuration service that generates config for skills, Playwright MCP,
/// and provider-specific execution resources such as Copilot bash environment files.
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
private const string ProjectMcpConfigFileName = ".mcp.json";
private static readonly string[] PlaywrightCommandArgs = ["-y", "@playwright/mcp@latest"];

	private readonly ISkillService _skillService;
	private static readonly List<string> _tempConfigFiles = new();
	private static readonly List<string> _tempArtifactDirectories = new();
	private static readonly object _tempFilesLock = new();
private static readonly JsonSerializerOptions JsonOptions = new()
{
WriteIndented = true,
PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
PropertyNameCaseInsensitive = true,
DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

	public McpConfigService(ISkillService skillService)
	{
		_skillService = skillService;
	}

	public async Task<string?> GenerateMcpConfigJsonAsync(Project? project = null, CancellationToken cancellationToken = default)
	{
		var skills = ProjectSkillHelper.GetExecutionSkills(project, await _skillService.GetEnabledAsync(cancellationToken));
		var projectMcpConfig = await LoadProjectMcpConfigAsync(project, project?.WorkingPath, cancellationToken);
		if (!RequiresMcp(skills, project, projectMcpConfig))
		{
			return null;
		}

		var config = BuildStandardMcpConfig(
			skills,
			project,
			CreatePlaywrightEnvironmentVariables(project, browserArtifactsDirectory: null),
			projectMcpConfig);
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
		var skills = ProjectSkillHelper.GetExecutionSkills(project, await _skillService.GetEnabledAsync(cancellationToken));
		var projectMcpConfig = await LoadProjectMcpConfigAsync(project, workingDirectory, cancellationToken);
		var requiresMcp = RequiresMcp(skills, project, projectMcpConfig);
		var bashEnvFilePath = providerType == ProviderType.Copilot
			? await CreateCopilotBashEnvFileAsync(cancellationToken)
			: null;
		if (!requiresMcp && string.IsNullOrWhiteSpace(bashEnvFilePath))
		{
			return null;
		}

		var browserArtifactsDirectory = requiresMcp && HasEnabledWebEnvironment(project)
			? CreateBrowserArtifactsDirectory()
			: null;
		string? filePath = null;
		if (requiresMcp)
		{
			filePath = CreateConfigFilePath(providerType, workingDirectory);
			var playwrightEnvironment = CreatePlaywrightEnvironmentVariables(project, browserArtifactsDirectory);
			var json = providerType == ProviderType.OpenCode
				? JsonSerializer.Serialize(BuildOpenCodeConfig(skills, project, playwrightEnvironment, projectMcpConfig), JsonOptions)
				: JsonSerializer.Serialize(BuildStandardMcpConfig(skills, project, playwrightEnvironment, projectMcpConfig), JsonOptions);

			await File.WriteAllTextAsync(filePath, json, cancellationToken);
			TrackTempFile(filePath);
		}

		if (!string.IsNullOrWhiteSpace(browserArtifactsDirectory))
		{
			TrackTempDirectory(browserArtifactsDirectory);
		}

		return new McpExecutionResources
		{
			ConfigFilePath = filePath,
			BrowserArtifactsDirectory = browserArtifactsDirectory,
			BashEnvFilePath = bashEnvFilePath
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

		if (!string.IsNullOrWhiteSpace(resources.BashEnvFilePath))
		{
			RemoveTrackedFile(resources.BashEnvFilePath);
			try
			{
				if (File.Exists(resources.BashEnvFilePath))
				{
					File.Delete(resources.BashEnvFilePath);
				}
			}
			catch
			{
				// Ignore cleanup errors
			}
		}
	}

private static bool RequiresMcp(IEnumerable<Skill> skills, Project? project, McpConfig? projectMcpConfig)
{
return skills.Any() || HasEnabledWebEnvironment(project) || projectMcpConfig?.McpServers.Count > 0;
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

	private static Dictionary<string, string>? CreatePlaywrightEnvironmentVariables(Project? project, string? browserArtifactsDirectory)
	{
		var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!string.IsNullOrWhiteSpace(browserArtifactsDirectory))
		{
			var temporaryDirectory = Path.Combine(browserArtifactsDirectory, "tmp");
			environmentVariables["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(browserArtifactsDirectory, "ms-playwright");
			environmentVariables["TMPDIR"] = temporaryDirectory;
			environmentVariables["TMP"] = temporaryDirectory;
			environmentVariables["TEMP"] = temporaryDirectory;
			environmentVariables["XDG_CACHE_HOME"] = Path.Combine(browserArtifactsDirectory, "cache");
		}

		var webEnvironments = project?.Environments
			.Where(environment => environment.IsEnabled && environment.Type == EnvironmentType.Web)
			.OrderByDescending(environment => environment.IsPrimary)
			.ThenBy(environment => environment.SortOrder)
			.ThenBy(environment => environment.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (webEnvironments?.Count > 0)
		{
			foreach (var environment in webEnvironments)
			{
				var sanitizedName = SanitizeEnvironmentName(environment.Name);
				if (!string.IsNullOrWhiteSpace(environment.Url))
				{
					environmentVariables[$"APP_{sanitizedName}_URL"] = environment.Url;
				}

				if (!string.IsNullOrWhiteSpace(environment.Username))
				{
					environmentVariables[$"APP_{sanitizedName}_USERNAME"] = environment.Username;
				}

				if (!string.IsNullOrWhiteSpace(environment.Password))
				{
					environmentVariables[$"APP_{sanitizedName}_PASSWORD"] = environment.Password;
				}
			}

			var primaryEnvironment = webEnvironments.FirstOrDefault(environment => environment.IsPrimary) ?? webEnvironments[0];
			if (!string.IsNullOrWhiteSpace(primaryEnvironment.Url))
			{
				environmentVariables["APP_URL"] = primaryEnvironment.Url;
			}

			if (!string.IsNullOrWhiteSpace(primaryEnvironment.Username))
			{
				environmentVariables["APP_USERNAME"] = primaryEnvironment.Username;
			}

			if (!string.IsNullOrWhiteSpace(primaryEnvironment.Password))
			{
				environmentVariables["APP_PASSWORD"] = primaryEnvironment.Password;
			}
		}

		return environmentVariables.Count > 0 ? environmentVariables : null;
	}

	private static async Task<McpConfig?> LoadProjectMcpConfigAsync(
		Project? project,
		string? workingDirectory,
		CancellationToken cancellationToken)
	{
		var configPath = ResolveProjectMcpConfigPath(project, workingDirectory);
		if (string.IsNullOrWhiteSpace(configPath))
		{
			return null;
		}

		var json = await File.ReadAllTextAsync(configPath, cancellationToken);
		using var document = JsonDocument.Parse(json);
		if (!document.RootElement.TryGetProperty("mcpServers", out var mcpServers) ||
			mcpServers.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var config = JsonSerializer.Deserialize<McpConfig>(json, JsonOptions);
		if (config == null)
		{
			return null;
		}

		config.McpServers ??= new Dictionary<string, McpServer>();
		return config;
	}

	private static string? ResolveProjectMcpConfigPath(Project? project, string? workingDirectory)
	{
		var searchRoots = new[]
		{
			workingDirectory,
			project?.WorkingPath
		}
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(path => Path.GetFullPath(path!))
			.Distinct(StringComparer.OrdinalIgnoreCase);

		foreach (var root in searchRoots)
		{
			if (!Directory.Exists(root))
			{
				continue;
			}

			var configPath = Path.Combine(root, ProjectMcpConfigFileName);
			if (File.Exists(configPath))
			{
				return configPath;
			}
		}

		return null;
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

	private static async Task<string?> CreateCopilotBashEnvFileAsync(CancellationToken cancellationToken)
	{
		if (OperatingSystem.IsWindows())
		{
			return null;
		}

		var tempDir = Path.Combine(Path.GetTempPath(), "vibeswarm", "copilot");
		Directory.CreateDirectory(tempDir);

		var filePath = Path.Combine(tempDir, $"copilot-bash-env-{Guid.NewGuid():N}.sh");
		var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(homeDirectory))
		{
			homeDirectory = Environment.GetEnvironmentVariable("HOME");
		}

		var enhancedPath = PlatformHelper.GetEnhancedPath(homeDirectory);
		var lines = new List<string>
		{
			$"export PATH={QuoteForBash(enhancedPath)}"
		};

		if (!string.IsNullOrWhiteSpace(homeDirectory))
		{
			lines.Add($"export HOME={QuoteForBash(homeDirectory)}");
		}

		lines.Add("if ! command -v fd >/dev/null 2>&1 && command -v fdfind >/dev/null 2>&1; then");
		lines.Add("	fd() { command fdfind \"$@\"; }");
		lines.Add("fi");

		await File.WriteAllTextAsync(filePath, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken);
		TrackTempFile(filePath);
		return filePath;
	}

	private static string QuoteForBash(string value)
	{
		return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
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
		Dictionary<string, string>? playwrightEnvironment,
		McpConfig? projectMcpConfig = null)
	{
		var config = new McpConfig
		{
			McpServers = new Dictionary<string, McpServer>(),
			AdditionalProperties = CloneExtensionData(projectMcpConfig?.AdditionalProperties)
		};

		if (projectMcpConfig?.McpServers != null)
		{
			foreach (var server in projectMcpConfig.McpServers)
			{
				config.McpServers[server.Key] = CloneMcpServer(server.Value);
			}
		}

foreach (var skill in skills)
{
var serverName = GetUniqueServerName(config.McpServers.Keys, SanitizeSkillName(skill.Name));
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
		Dictionary<string, string>? playwrightEnvironment,
		McpConfig? projectMcpConfig = null)
	{
		var config = new OpenCodeMcpConfig();

		if (projectMcpConfig?.McpServers != null)
		{
			foreach (var server in projectMcpConfig.McpServers)
			{
				config.Mcp[server.Key] = ConvertToOpenCodeMcpServer(server.Value);
			}
		}

foreach (var skill in skills)
{
var serverName = GetUniqueServerName(config.Mcp.Keys, SanitizeSkillName(skill.Name));
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

	private static string SanitizeEnvironmentName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "ENV";
		}

		var sanitized = new string(name
			.ToUpperInvariant()
			.Select(character => char.IsLetterOrDigit(character) ? character : '_')
			.ToArray())
			.Trim('_');

		while (sanitized.Contains("__", StringComparison.Ordinal))
		{
			sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);
		}

	return string.IsNullOrEmpty(sanitized) ? "ENV" : sanitized;
	}

	private static McpServer CloneMcpServer(McpServer server)
	{
		return new McpServer
		{
			Command = server.Command,
			Args = server.Args?.ToArray(),
			Env = server.Env != null
				? new Dictionary<string, string>(server.Env, StringComparer.Ordinal)
				: null,
			AdditionalProperties = CloneExtensionData(server.AdditionalProperties)
		};
	}

	private static OpenCodeMcpServer ConvertToOpenCodeMcpServer(McpServer server)
	{
		var additionalProperties = CloneExtensionData(server.AdditionalProperties, "type", "enabled");
		var command = new List<string>();
		if (!string.IsNullOrWhiteSpace(server.Command))
		{
			command.Add(server.Command);
		}

		if (server.Args != null)
		{
			command.AddRange(server.Args.Where(argument => !string.IsNullOrWhiteSpace(argument)));
		}

		var type = "local";
		if (server.AdditionalProperties?.TryGetValue("type", out var typeElement) == true &&
			typeElement.ValueKind == JsonValueKind.String &&
			!string.IsNullOrWhiteSpace(typeElement.GetString()))
		{
			type = typeElement.GetString()!;
		}
		else if (additionalProperties?.ContainsKey("url") == true)
		{
			type = "remote";
		}

		var enabled = true;
		if (server.AdditionalProperties?.TryGetValue("enabled", out var enabledElement) == true &&
			(enabledElement.ValueKind == JsonValueKind.True || enabledElement.ValueKind == JsonValueKind.False))
		{
			enabled = enabledElement.GetBoolean();
		}

		return new OpenCodeMcpServer
		{
			Type = type,
			Command = command.Count > 0 ? [..command] : null,
			Enabled = enabled,
			Environment = server.Env != null
				? new Dictionary<string, string>(server.Env, StringComparer.Ordinal)
				: null,
			AdditionalProperties = additionalProperties
		};
	}

	private static Dictionary<string, JsonElement>? CloneExtensionData(
		Dictionary<string, JsonElement>? extensionData,
		params string[] excludedKeys)
	{
		if (extensionData == null || extensionData.Count == 0)
		{
			return null;
		}

		var excluded = excludedKeys.Length > 0
			? new HashSet<string>(excludedKeys, StringComparer.OrdinalIgnoreCase)
			: null;
		var clone = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		foreach (var property in extensionData)
		{
			if (excluded?.Contains(property.Key) == true)
			{
				continue;
			}

			clone[property.Key] = property.Value.Clone();
		}

		return clone.Count > 0 ? clone : null;
	}
}

/// <summary>
/// MCP configuration format for Copilot and Claude
/// </summary>
public class McpConfig
{
[JsonPropertyName("mcpServers")]
public Dictionary<string, McpServer> McpServers { get; set; } = new();

[JsonExtensionData]
public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public class McpServer
{
[JsonPropertyName("command")]
public string Command { get; set; } = string.Empty;

[JsonPropertyName("args")]
public string[]? Args { get; set; }

[JsonPropertyName("env")]
public Dictionary<string, string>? Env { get; set; }

[JsonExtensionData]
public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
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

[JsonExtensionData]
public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
