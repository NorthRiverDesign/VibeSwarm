using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

public class CommonProviderSetupService(VibeSwarmDbContext dbContext) : ICommonProviderSetupService
{
	private readonly VibeSwarmDbContext _dbContext = dbContext;

	public async Task<IReadOnlyList<CommonProviderSetupStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
	{
		var configuredProviders = await _dbContext.Providers
			.Where(provider => provider.ConnectionMode == ProviderConnectionMode.CLI)
			.ToListAsync(cancellationToken);

		var statuses = new List<CommonProviderSetupStatus>();
		foreach (var providerType in GetSupportedProviderTypes())
		{
			var preferredProvider = configuredProviders
				.Where(provider => provider.Type == providerType)
				.OrderBy(provider => provider.Name)
				.FirstOrDefault();

			var installSpec = GetInstallSpec(providerType);
			var installStatus = await DetectInstallationAsync(providerType, cancellationToken);
			var authStatus = DetectAuthentication(providerType, preferredProvider);

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
				HasConfiguredProvider = preferredProvider != null,
				ProviderId = preferredProvider?.Id,
				ProviderName = preferredProvider?.Name,
				IsAuthenticated = authStatus.IsAuthenticated,
				AuthenticationStatus = authStatus.Message
			});
		}

		return statuses;
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
		if (!string.IsNullOrWhiteSpace(provider?.ApiKey))
		{
			return new AuthenticationState(true, "Saved in VibeSwarm.");
		}

		return providerType switch
		{
			ProviderType.Claude when File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"))
				=> new AuthenticationState(true, "Claude Code browser login detected on host."),
			ProviderType.Copilot when !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GH_TOKEN")) ||
				!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN"))
				=> new AuthenticationState(true, "Using host GitHub token environment variables."),
			ProviderType.OpenCode when TryReadOpenCodeAuthStatus()
				=> new AuthenticationState(true, "OpenCode auth.json detected on host."),
			ProviderType.Claude => new AuthenticationState(false, "Save an Anthropic API key or complete Claude's browser login on the host."),
			ProviderType.Copilot => new AuthenticationState(false, "Save a GitHub token to let VibeSwarm authenticate the Copilot CLI."),
			ProviderType.OpenCode => new AuthenticationState(false, "Save an OpenCode API key to populate the host auth.json."),
			_ => new AuthenticationState(false, null)
		};
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

	private async Task<InstallStatus> DetectInstallationAsync(ProviderType providerType, CancellationToken cancellationToken)
	{
		var installSpec = GetInstallSpec(providerType);
		var result = await RunExecutableAsync(installSpec.ExecutableName, installSpec.VersionArguments, cancellationToken);
		if (!result.Success)
		{
			return new InstallStatus(false, null);
		}

		return new InstallStatus(true, NormalizeVersion(result.Output));
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
				Arguments = arguments,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

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
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			foreach (var argument in shell.Arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}

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
	private sealed record InstallStatus(bool IsInstalled, string? Version);
	private sealed record ProcessResult(bool Success, string? Output, string? ErrorMessage);
	private sealed record ShellCommand(string FileName, IReadOnlyList<string> Arguments);
}
