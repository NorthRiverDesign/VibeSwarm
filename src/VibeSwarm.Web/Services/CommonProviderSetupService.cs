using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

public class CommonProviderSetupService(
	VibeSwarmDbContext dbContext,
	ProviderCliDetectionService providerCliDetectionService,
	AgentDetectionService agentDetectionService) : ICommonProviderSetupService
{
	private readonly VibeSwarmDbContext _dbContext = dbContext;
	private readonly ProviderCliDetectionService _providerCliDetectionService = providerCliDetectionService;
	private readonly AgentDetectionService _agentDetectionService = agentDetectionService;

	public async Task<IReadOnlyList<CommonProviderSetupStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
	{
		var supportedProviderTypes = GetSupportedProviderTypes();
		var configuredProviders = await _dbContext.Providers
			.Where(provider => supportedProviderTypes.Contains(provider.Type))
			.ToListAsync(cancellationToken);

		var statuses = new List<CommonProviderSetupStatus>();
		foreach (var providerType in supportedProviderTypes)
		{
			var providersForType = configuredProviders
				.Where(provider => provider.Type == providerType)
				.OrderByDescending(provider => provider.IsDefault)
				.ThenBy(provider => provider.Name)
				.ToList();

			var preferredCliProvider = providersForType
				.FirstOrDefault(provider => provider.ConnectionMode == ProviderConnectionMode.CLI);
			var preferredAuthProvider = providersForType.FirstOrDefault();

			var installSpec = GetInstallSpec(providerType);
			var installStatus = await _providerCliDetectionService.DetectAsync(
				providerType,
				installSpec.ExecutableName,
				installSpec.VersionArguments,
				preferredCliProvider?.ExecutablePath,
				cancellationToken: cancellationToken);
			var authStatus = DetectAuthentication(providerType, preferredAuthProvider);
			var authConnectionMode = preferredAuthProvider?.ConnectionMode ?? ProviderCapabilities.GetDefaultMode(providerType);

			statuses.Add(new CommonProviderSetupStatus
			{
				ProviderType = providerType,
				DisplayName = GetDisplayName(providerType),
				Description = GetDescription(providerType),
				DocumentationUrl = GetDocumentationUrl(providerType),
				ExecutableName = installSpec.ExecutableName,
				InstallMethodLabel = installSpec.Label,
				InstallCommand = installSpec.Command,
				ApiKeyLabel = GetApiKeyLabel(providerType),
				ApiKeyPlaceholder = GetApiKeyPlaceholder(providerType),
				ApiKeyHelpText = GetApiKeyHelpText(providerType),
				IsInstalled = installStatus.IsInstalled,
				InstalledVersion = installStatus.Version,
				ResolvedExecutablePath = installStatus.ResolvedExecutablePath,
				InstallationStatus = installStatus.Message,
				HasConfiguredProvider = providersForType.Count > 0,
				ProviderId = preferredAuthProvider?.Id,
				ProviderName = preferredAuthProvider?.Name,
				AuthenticationConnectionMode = authConnectionMode,
				IsAuthenticated = authStatus.IsAuthenticated,
				AuthenticationStatus = authStatus.Message,
				ConfiguredProviders = providersForType
					.Select(provider => new CommonProviderSetupConfiguredProvider
					{
						Id = provider.Id,
						Name = provider.Name,
						ConnectionMode = provider.ConnectionMode,
						IsDefault = provider.IsDefault
					})
					.ToList()
			});
		}

		return statuses;
	}

	public async Task<IReadOnlyList<CommonProviderSetupStatus>> RefreshAsync(CancellationToken cancellationToken = default)
	{
		await _agentDetectionService.DetectAndRegisterAsync(cancellationToken);
		return await GetStatusesAsync(cancellationToken);
	}

	public async Task<CommonProviderActionResult> InstallAsync(ProviderType providerType, CancellationToken cancellationToken = default)
	{
		var installSpec = GetInstallSpec(providerType);
		var result = await RunShellCommandAsync(installSpec.Command, TimeSpan.FromMinutes(10), cancellationToken);
		if (!result.Success)
		{
			return new CommonProviderActionResult
			{
				Success = false,
				ErrorMessage = result.ErrorMessage ?? $"Failed to install {GetDisplayName(providerType)}."
			};
		}

		await _agentDetectionService.DetectAndRegisterAsync(cancellationToken);
		var status = await GetStatusAsync(providerType, cancellationToken);
		return new CommonProviderActionResult
		{
			Success = true,
			Message = $"{GetDisplayName(providerType)} installation completed.",
			Status = status
		};
	}

	public async Task<CommonProviderActionResult> SaveAuthenticationAsync(CommonProviderSetupRequest request, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(request.ApiKey))
		{
			return new CommonProviderActionResult
			{
				Success = false,
				ErrorMessage = $"{GetApiKeyLabel(request.ProviderType)} is required."
			};
		}

		var provider = await _dbContext.Providers
			.Where(item => item.Type == request.ProviderType && item.ConnectionMode == ProviderConnectionMode.CLI)
			.OrderBy(item => item.Name)
			.FirstOrDefaultAsync(cancellationToken);

		if (provider == null)
		{
			provider = new Provider
			{
				Id = Guid.NewGuid(),
				Name = await GetAvailableProviderNameAsync(request.ProviderType, cancellationToken),
				Type = request.ProviderType,
				ConnectionMode = ProviderConnectionMode.CLI,
				IsEnabled = true,
				CreatedAt = DateTime.UtcNow,
				ConfiguredLimitType = request.ProviderType == ProviderType.Copilot
					? UsageLimitType.PremiumRequests
					: UsageLimitType.None
			};

			_dbContext.Providers.Add(provider);
		}

		provider.ApiKey = request.ApiKey.Trim();
		provider.IsEnabled = true;

		if (request.ProviderType == ProviderType.OpenCode)
		{
			await WriteOpenCodeAuthFileAsync(provider.ApiKey, cancellationToken);
		}

		await _dbContext.SaveChangesAsync(cancellationToken);

		var status = await GetStatusAsync(request.ProviderType, cancellationToken);
		return new CommonProviderActionResult
		{
			Success = true,
			Message = $"{GetDisplayName(request.ProviderType)} authentication saved.",
			Status = status
		};
	}

	private async Task<string> GetAvailableProviderNameAsync(ProviderType providerType, CancellationToken cancellationToken)
	{
		var defaultName = GetDefaultProviderName(providerType);
		if (!await _dbContext.Providers.AnyAsync(provider => provider.Name == defaultName, cancellationToken))
		{
			return defaultName;
		}

		var candidate = $"{defaultName} CLI";
		var suffix = 2;
		while (await _dbContext.Providers.AnyAsync(provider => provider.Name == candidate, cancellationToken))
		{
			candidate = $"{defaultName} CLI {suffix}";
			suffix++;
		}

		return candidate;
	}

	private async Task<CommonProviderSetupStatus> GetStatusAsync(ProviderType providerType, CancellationToken cancellationToken)
	{
		return (await GetStatusesAsync(cancellationToken)).First(status => status.ProviderType == providerType);
	}

	private static IReadOnlyList<ProviderType> GetSupportedProviderTypes()
		=> [ProviderType.Claude, ProviderType.Copilot, ProviderType.OpenCode];

	private static string GetDisplayName(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "Claude Code",
		ProviderType.Copilot => "GitHub Copilot",
		ProviderType.OpenCode => "OpenCode",
		_ => providerType.ToString()
	};

	private static string GetDescription(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "Install Claude Code on this host and save an Anthropic key for VibeSwarm-managed CLI runs.",
		ProviderType.Copilot => "Install the GitHub Copilot CLI and save a fine-grained PAT with Copilot Requests permission.",
		ProviderType.OpenCode => "Install OpenCode and store an OpenCode API key so the host can launch jobs immediately.",
		_ => string.Empty
	};

	private static string GetDocumentationUrl(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "https://code.claude.com/docs/en/setup",
		ProviderType.Copilot => "https://docs.github.com/copilot/concepts/agents/about-copilot-cli",
		ProviderType.OpenCode => "https://opencode.ai/docs",
		_ => string.Empty
	};

	private static string GetApiKeyLabel(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "Anthropic API Key",
		ProviderType.Copilot => "GitHub Token",
		ProviderType.OpenCode => "OpenCode API Key",
		_ => "API Key"
	};

	private static string GetApiKeyPlaceholder(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "sk-ant-...",
		ProviderType.Copilot => "github_pat_...",
		ProviderType.OpenCode => "oc_...",
		_ => string.Empty
	};

	private static string GetApiKeyHelpText(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "Claude Code normally supports browser login, but VibeSwarm can fully automate it by using an Anthropic API key instead.",
		ProviderType.Copilot => "Use a fine-grained PAT with the Copilot Requests permission. VibeSwarm injects it as GH_TOKEN/GITHUB_TOKEN when launching the CLI.",
		ProviderType.OpenCode => "Create an API key at opencode.ai/auth. VibeSwarm also writes it to OpenCode's auth.json so the host stays configured.",
		_ => string.Empty
	};

	private static string GetDefaultProviderName(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "Claude Code",
		ProviderType.Copilot => "GitHub Copilot",
		ProviderType.OpenCode => "OpenCode",
		_ => providerType.ToString()
	};

	private static InstallSpec GetInstallSpec(ProviderType providerType)
	{
		var isWindows = OperatingSystem.IsWindows();
		return providerType switch
		{
			ProviderType.Claude => new InstallSpec(
				ExecutableName: isWindows ? "claude.exe" : "claude",
				Label: isWindows ? "PowerShell installer" : "Official installer",
				Command: isWindows
					? "irm https://claude.ai/install.ps1 | iex"
					: "curl -fsSL https://claude.ai/install.sh | bash",
				VersionArguments: "--version"),
			ProviderType.Copilot => new InstallSpec(
				ExecutableName: isWindows ? "copilot.exe" : "copilot",
				Label: isWindows ? "WinGet installer" : "Official installer",
				Command: isWindows
					? "winget install --id GitHub.Copilot -e --accept-source-agreements --accept-package-agreements"
					: "curl -fsSL https://gh.io/copilot-install | bash",
				VersionArguments: "--binary-version"),
			ProviderType.OpenCode => new InstallSpec(
				ExecutableName: isWindows ? "opencode.exe" : "opencode",
				Label: isWindows ? "npm installer" : "Official installer",
				Command: isWindows
					? "npm install -g opencode-ai"
					: "curl -fsSL https://opencode.ai/install | bash",
				VersionArguments: "--version"),
			_ => throw new NotSupportedException($"Provider type '{providerType}' is not supported for quick setup.")
		};
	}

	private static AuthenticationState DetectAuthentication(ProviderType providerType, Provider? provider)
	{
		var connectionMode = provider?.ConnectionMode ?? ProviderCapabilities.GetDefaultMode(providerType);
		if (!string.IsNullOrWhiteSpace(provider?.ApiKey))
		{
			return new AuthenticationState(true, $"Saved in VibeSwarm for this {connectionMode} connection.");
		}

		return (providerType, connectionMode) switch
		{
			(ProviderType.Claude, ProviderConnectionMode.CLI) when File.Exists(Path.Combine(GetUserHomeDirectory(), ".claude.json"))
				=> new AuthenticationState(true, "Claude Code browser login detected on host for this CLI connection."),
			(ProviderType.Copilot, ProviderConnectionMode.CLI or ProviderConnectionMode.SDK) when HasCopilotEnvironmentToken()
				=> new AuthenticationState(true, $"Using host GitHub token environment variables for this {connectionMode} connection."),
			(ProviderType.Copilot, ProviderConnectionMode.CLI or ProviderConnectionMode.SDK) when TryReadCopilotAuthStatus()
				=> new AuthenticationState(true, $"Copilot CLI login detected on host for this {connectionMode} connection."),
			(ProviderType.OpenCode, ProviderConnectionMode.CLI) when TryReadOpenCodeAuthStatus()
				=> new AuthenticationState(true, "OpenCode auth.json detected on host for this CLI connection."),
			(ProviderType.Claude, ProviderConnectionMode.CLI)
				=> new AuthenticationState(false, "Save an Anthropic API key or complete Claude's browser login on the host for this CLI connection."),
			(ProviderType.Claude, ProviderConnectionMode.SDK)
				=> new AuthenticationState(false, "Save an Anthropic API key for this SDK connection."),
			(ProviderType.Copilot, ProviderConnectionMode.CLI)
				=> new AuthenticationState(false, "Sign in with 'copilot login' or save a GitHub token for this CLI connection."),
			(ProviderType.Copilot, ProviderConnectionMode.SDK)
				=> new AuthenticationState(false, "Save a GitHub token or sign in with 'copilot login' so this SDK connection can use the Copilot CLI session."),
			(ProviderType.OpenCode, ProviderConnectionMode.CLI)
				=> new AuthenticationState(false, "Save an OpenCode API key to populate the host auth.json for this CLI connection."),
			(ProviderType.OpenCode, ProviderConnectionMode.REST)
				=> new AuthenticationState(false, "Save an OpenCode API key for this REST connection."),
			_ => new AuthenticationState(false, null)
		};
	}

	private static bool HasCopilotEnvironmentToken()
	{
		return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN")) ||
			!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GH_TOKEN")) ||
			!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
	}

	internal static bool TryReadCopilotAuthStatus()
	{
		var configPath = Path.Combine(GetUserHomeDirectory(), ".copilot", "config.json");
		if (!File.Exists(configPath))
		{
			return false;
		}

		try
		{
			using var stream = File.OpenRead(configPath);
			using var document = JsonDocument.Parse(stream);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			return HasNonEmptyObjectOrArray(document.RootElement, "last_logged_in_user") ||
				HasNonEmptyObjectOrArray(document.RootElement, "logged_in_users") ||
				HasNonEmptyObjectOrArray(document.RootElement, "copilot_tokens");
		}
		catch
		{
			return false;
		}
	}

	private static bool HasNonEmptyObjectOrArray(JsonElement root, string propertyName)
	{
		if (!root.TryGetProperty(propertyName, out var property))
		{
			return false;
		}

		return property.ValueKind switch
		{
			JsonValueKind.Array => property.GetArrayLength() > 0,
			JsonValueKind.Object => property.EnumerateObject().Any(),
			JsonValueKind.String => !string.IsNullOrWhiteSpace(property.GetString()),
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Null => false,
			JsonValueKind.Undefined => false,
			_ => true
		};
	}

	private static string GetUserHomeDirectory()
	{
		var homeDirectory = Environment.GetEnvironmentVariable("HOME");
		if (!string.IsNullOrWhiteSpace(homeDirectory))
		{
			return homeDirectory;
		}

		return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	}

	private static bool TryReadOpenCodeAuthStatus()
	{
		var authPath = GetOpenCodeAuthPath();
		if (!File.Exists(authPath))
		{
			return false;
		}

		try
		{
			using var stream = File.OpenRead(authPath);
			using var document = JsonDocument.Parse(stream);
			return document.RootElement.ValueKind == JsonValueKind.Object &&
				document.RootElement.TryGetProperty("opencode", out _);
		}
		catch
		{
			return false;
		}
	}

	private static string? NormalizeVersion(string? output)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return null;
		}

		var firstLine = output
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault();

		return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
	}

	private static async Task<ProcessResult> RunExecutableAsync(string fileName, string arguments, CancellationToken cancellationToken)
	{
		try
		{
			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			var startInfo = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments
			};

			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var process = new Process { StartInfo = startInfo };
			process.Start();

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
					// Ignore best-effort cleanup failures.
				}

				return new ProcessResult(false, null, "Timed out while detecting the installed CLI version.");
			}

			var output = await outputTask;
			var error = await errorTask;

			return process.ExitCode == 0
				? new ProcessResult(true, output, null)
				: new ProcessResult(false, output, string.IsNullOrWhiteSpace(error) ? output : error);
		}
		catch (Exception ex)
		{
			return new ProcessResult(false, null, ex.Message);
		}
	}

	private static async Task<ProcessResult> RunShellCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var shell = GetShellCommand(command);
		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = shell.FileName,
			};

			foreach (var argument in shell.Arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}

			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var process = new Process { StartInfo = startInfo };
			using var timeoutCts = new CancellationTokenSource(timeout);
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

			process.Start();

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
					// Ignore best-effort cleanup failures.
				}

				return new ProcessResult(false, null, "The installer timed out before it finished.");
			}

			var output = await outputTask;
			var error = await errorTask;
			return process.ExitCode == 0
				? new ProcessResult(true, output, null)
				: new ProcessResult(false, output, string.IsNullOrWhiteSpace(error) ? output : error);
		}
		catch (Exception ex)
		{
			return new ProcessResult(false, null, ex.Message);
		}
	}

	private static ShellCommand GetShellCommand(string command)
	{
		if (OperatingSystem.IsWindows())
		{
			return new ShellCommand("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command]);
		}

		return new ShellCommand("/bin/bash", ["-lc", command]);
	}

	private static string GetOpenCodeAuthPath()
	{
		var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		return Path.Combine(dataDirectory, "opencode", "auth.json");
	}

	private static async Task WriteOpenCodeAuthFileAsync(string apiKey, CancellationToken cancellationToken)
	{
		var authPath = GetOpenCodeAuthPath();
		var authDirectory = Path.GetDirectoryName(authPath);
		if (!string.IsNullOrWhiteSpace(authDirectory))
		{
			Directory.CreateDirectory(authDirectory);
		}

		Dictionary<string, JsonElement> existingEntries = [];
		if (File.Exists(authPath))
		{
			try
			{
				await using var existingStream = File.OpenRead(authPath);
				existingEntries = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(existingStream, cancellationToken: cancellationToken) ?? [];
			}
			catch
			{
				existingEntries = [];
			}
		}

		var updatedEntries = new Dictionary<string, object?>();
		foreach (var (key, value) in existingEntries)
		{
			updatedEntries[key] = value;
		}

		updatedEntries["opencode"] = new
		{
			type = "api",
			key = apiKey
		};

		await using var outputStream = File.Create(authPath);
		await JsonSerializer.SerializeAsync(outputStream, updatedEntries, new JsonSerializerOptions
		{
			WriteIndented = true
		}, cancellationToken);
	}

	private sealed record InstallSpec(string ExecutableName, string Label, string Command, string VersionArguments);
	private sealed record AuthenticationState(bool IsAuthenticated, string? Message);
	private sealed record ProcessResult(bool Success, string? Output, string? ErrorMessage);
	private sealed record ShellCommand(string FileName, IReadOnlyList<string> Arguments);
}
