using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Services;

public class CommonProviderSetupService(
	VibeSwarmDbContext dbContext,
	ProviderCliDetectionService providerCliDetectionService,
	AgentDetectionService agentDetectionService,
	IProviderSignInService? providerSignInService = null) : ICommonProviderSetupService
{
	/// <summary>Claude Code warns this long before a stored sign-in ends; the host row does too.</summary>
	private static readonly TimeSpan ClaudeLoginEndingWarning = TimeSpan.FromDays(10);

	private readonly VibeSwarmDbContext _dbContext = dbContext;
	private readonly ProviderCliDetectionService _providerCliDetectionService = providerCliDetectionService;
	private readonly AgentDetectionService _agentDetectionService = agentDetectionService;
	private readonly IProviderSignInService? _providerSignInService = providerSignInService;

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
			var preferredAuthProvider = SelectAuthenticationTargetProvider(providerType, providersForType) ?? providersForType.FirstOrDefault();

			var installSpec = GetInstallSpec(providerType);
			var installStatus = await _providerCliDetectionService.DetectAsync(
				providerType,
				installSpec.ExecutableName,
				installSpec.VersionArguments,
				preferredCliProvider?.ExecutablePath,
				cancellationToken: cancellationToken);
			var hostTools = await GetHostToolsAsync(providerType, cancellationToken);
			var authStatus = await DetectAuthenticationAsync(
				providerType,
				preferredAuthProvider,
				preferredCliProvider?.ExecutablePath ?? installStatus.ResolvedExecutablePath,
				cancellationToken);
			var authConnectionMode = preferredAuthProvider?.ConnectionMode ?? ProviderCapabilities.GetDefaultMode(providerType);
			var signIn = _providerSignInService == null ? null : await _providerSignInService.GetAsync(providerType, cancellationToken);

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
				AuthenticationTypeLabel = authStatus.TypeLabel,
				IsAuthenticated = authStatus.IsAuthenticated,
				AuthenticationStatus = authStatus.Message,
				ActiveSignIn = signIn is { IsActive: true } ? signIn : null,
				HostTools = hostTools,
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

		var providersForType = await _dbContext.Providers
			.Where(item => item.Type == request.ProviderType)
			.OrderByDescending(item => item.IsDefault)
			.ThenBy(item => item.Name)
			.ToListAsync(cancellationToken);
		var provider = SelectAuthenticationTargetProvider(request.ProviderType, providersForType);
		if (request.ProviderType == ProviderType.Copilot && provider != null && ProviderCapabilities.IsCopilotCustomProvider(provider))
		{
			provider = null;
		}

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
		ProviderType.Copilot => "Install the GitHub Copilot CLI and save a fine-grained PAT with Copilot Requests permission. Install ripgrep and fd/fdfind on the host for faster autonomous file discovery.",
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
		ProviderType.Copilot => "Use a fine-grained PAT with the Copilot Requests permission. VibeSwarm injects it as GH_TOKEN/GITHUB_TOKEN when launching the CLI and prepares a bash environment so Copilot can find host search tools like rg and fd/fdfind.",
		ProviderType.OpenCode => "Create an API key at opencode.ai/auth. VibeSwarm also writes it to OpenCode's auth.json so the host stays configured.",
		_ => string.Empty
	};

	private async Task<List<CommonProviderHostToolStatus>> GetHostToolsAsync(ProviderType providerType, CancellationToken cancellationToken)
	{
		if (providerType != ProviderType.Copilot)
		{
			return [];
		}

		var ripgrepStatus = await _providerCliDetectionService.DetectAnyAsync(
			["rg"],
			"--version",
			cancellationToken: cancellationToken);
		var fileSearchStatus = await _providerCliDetectionService.DetectAnyAsync(
			["fd", "fdfind"],
			"--version",
			cancellationToken: cancellationToken);

		return
		[
			new CommonProviderHostToolStatus
			{
				Name = "ripgrep",
				Command = "rg",
				Purpose = "Fast content search for autonomous CLI work.",
				IsInstalled = ripgrepStatus.IsInstalled,
				ResolvedExecutablePath = ripgrepStatus.ResolvedExecutablePath,
				Status = ripgrepStatus.Message
			},
			new CommonProviderHostToolStatus
			{
				Name = "fd",
				Command = "fd / fdfind",
				Purpose = "Fast file-name search. Debian and Ubuntu often install this command as fdfind.",
				IsInstalled = fileSearchStatus.IsInstalled,
				ResolvedExecutablePath = fileSearchStatus.ResolvedExecutablePath,
				Status = fileSearchStatus.Message
			}
		];
	}

	private static string GetDefaultProviderName(ProviderType providerType) => providerType switch
	{
		ProviderType.Claude => "Claude Code",
		ProviderType.Copilot => "GitHub Copilot",
		ProviderType.OpenCode => "OpenCode",
		_ => providerType.ToString()
	};

	internal static InstallSpec GetInstallSpec(ProviderType providerType)
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

	private async Task<AuthenticationState> DetectAuthenticationAsync(
		ProviderType providerType,
		Provider? provider,
		string? executablePath,
		CancellationToken cancellationToken)
	{
		var connectionMode = provider?.ConnectionMode ?? ProviderCapabilities.GetDefaultMode(providerType);

		if (providerType == ProviderType.Copilot && provider != null && ProviderCapabilities.IsCopilotCustomProvider(provider))
		{
			return !string.IsNullOrWhiteSpace(provider.ApiKey)
				? new AuthenticationState(true, "Saved in VibeSwarm for this SDK custom provider connection.", "Custom Provider")
				: new AuthenticationState(false, "Save the custom provider API key for this SDK custom provider connection.", "Custom Provider");
		}

		if (!string.IsNullOrWhiteSpace(provider?.ApiKey))
		{
			return new AuthenticationState(
				true,
				$"Saved in VibeSwarm for this {connectionMode} connection.",
				ProviderCapabilities.GetConnectionTypeLabel(provider));
		}

		if (providerType == ProviderType.Copilot)
		{
			if (HasCopilotEnvironmentToken())
			{
				return new AuthenticationState(true, $"Using host GitHub token environment variables for this {connectionMode} connection.", "GitHub Token");
			}

			if (TryReadCopilotAuthStatus())
			{
				return new AuthenticationState(true, $"Copilot CLI login detected on host for this {connectionMode} connection.", "Logged-in User");
			}

			var sdkAuthStatus = await TryGetCopilotAuthenticationStateAsync(executablePath, cancellationToken);
			if (sdkAuthStatus != null)
			{
				return sdkAuthStatus;
			}
		}

		if (providerType == ProviderType.Claude &&
			connectionMode == ProviderConnectionMode.CLI &&
			File.Exists(Path.Combine(GetUserHomeDirectory(), ".claude.json")) &&
			await TryReadClaudeLoginAsync(executablePath, cancellationToken) is { } claudeLogin)
		{
			return DescribeClaudeLogin(claudeLogin, DateTime.UtcNow);
		}

		return (providerType, connectionMode) switch
		{
			(ProviderType.Claude, ProviderConnectionMode.CLI) when File.Exists(Path.Combine(GetUserHomeDirectory(), ".claude.json"))
				=> new AuthenticationState(true, "Claude Code browser login detected on host for this CLI connection.", "Browser Login"),
			(ProviderType.OpenCode, ProviderConnectionMode.CLI) when TryReadOpenCodeAuthStatus()
				=> new AuthenticationState(true, "OpenCode auth.json detected on host for this CLI connection.", "API Key"),
			(ProviderType.Claude, ProviderConnectionMode.CLI)
				=> new AuthenticationState(false, "Sign in, or save an Anthropic API key, for this CLI connection.", "Browser Login"),
			(ProviderType.Claude, ProviderConnectionMode.SDK)
				=> new AuthenticationState(false, "Save an Anthropic API key for this SDK connection.", "API Key"),
			(ProviderType.Copilot, ProviderConnectionMode.CLI)
				=> new AuthenticationState(false, "Sign in, or save a GitHub token, for this CLI connection.", "Logged-in User"),
			(ProviderType.Copilot, ProviderConnectionMode.SDK)
				=> new AuthenticationState(false, "Sign in, or save a GitHub token, so this SDK connection can use the Copilot CLI session.", "Logged-in User"),
			(ProviderType.OpenCode, ProviderConnectionMode.CLI)
				=> new AuthenticationState(false, "Save an OpenCode API key to populate the host auth.json for this CLI connection.", "API Key"),
			(ProviderType.OpenCode, ProviderConnectionMode.REST)
				=> new AuthenticationState(false, "Save an OpenCode API key for this REST connection.", "API Key"),
			_ => new AuthenticationState(false, null, null)
		};
	}

	/// <summary>
	/// Asks Claude Code whether it is signed in. The <c>.claude.json</c> file outlives an expired
	/// login, so it alone can't tell. Null when the CLI can't say (an older version, a timeout).
	/// Its output names the account, so it is never logged.
	/// </summary>
	private static async Task<ClaudeLoginStatus?> TryReadClaudeLoginAsync(string? executablePath, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(executablePath))
		{
			return null;
		}

		try
		{
			var startInfo = new ProcessStartInfo { FileName = executablePath };
			foreach (var argument in new[] { "auth", "status", "--json" })
			{
				startInfo.ArgumentList.Add(argument);
			}

			PlatformHelper.ConfigureForCrossPlatform(startInfo);

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));

			using var process = new Process { StartInfo = startInfo };
			process.Start();
			process.StandardInput.Close();
			var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
			var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
			try
			{
				await process.WaitForExitAsync(timeout.Token);
				await Task.WhenAll(outputTask, errorTask);
			}
			catch (OperationCanceledException)
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch
				{
					// Best effort.
				}

				return null;
			}

			// Signed out, it exits non-zero but still prints the JSON.
			return ParseClaudeAuthStatus(outputTask.Result);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}

	internal static ClaudeLoginStatus? ParseClaudeAuthStatus(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object ||
				!root.TryGetProperty("loggedIn", out var loggedIn) ||
				loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			{
				return null;
			}

			DateTime? expiresAt = root.TryGetProperty("loginExpiresAt", out var expires) &&
				expires.ValueKind == JsonValueKind.String &&
				DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
					? parsed.UtcDateTime
					: null;
			return new ClaudeLoginStatus(loggedIn.GetBoolean(), expiresAt);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	internal static AuthenticationState DescribeClaudeLogin(ClaudeLoginStatus login, DateTime utcNow)
	{
		if (!login.LoggedIn)
		{
			return new AuthenticationState(false, "Claude Code is signed out on this host. Sign in again, or save an Anthropic API key, to run jobs.", "Browser Login");
		}

		if (login.ExpiresAt is { } expiresAt && expiresAt <= utcNow)
		{
			return new AuthenticationState(false, "Claude Code's sign-in on this host has expired. Sign in again to run jobs.", "Browser Login");
		}

		if (login.ExpiresAt is { } endsAt && endsAt - utcNow <= ClaudeLoginEndingWarning)
		{
			return new AuthenticationState(true, $"Claude Code's sign-in on this host ends {endsAt.ToString("MMM d", CultureInfo.InvariantCulture)}. Sign in again before then so jobs keep running.", "Browser Login");
		}

		return new AuthenticationState(true, "Claude Code browser login detected on host for this CLI connection.", "Browser Login");
	}

	private static bool HasCopilotEnvironmentToken()
	{
		return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN")) ||
			!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GH_TOKEN")) ||
			!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
	}

	private static Provider? SelectAuthenticationTargetProvider(ProviderType providerType, IReadOnlyList<Provider> providersForType)
	{
		var preferredProvider = providersForType.FirstOrDefault();
		if (preferredProvider == null)
		{
			return null;
		}

		return providerType switch
		{
			ProviderType.Copilot when ProviderCapabilities.IsCopilotCustomProvider(preferredProvider)
				=> providersForType.FirstOrDefault(item => item.ConnectionMode == ProviderConnectionMode.CLI) ?? preferredProvider,
			ProviderType.OpenCode
				=> providersForType.FirstOrDefault(item => item.ConnectionMode == ProviderConnectionMode.CLI) ?? preferredProvider,
			_ => preferredProvider
		};
	}

	private static async Task<AuthenticationState?> TryGetCopilotAuthenticationStateAsync(string? executablePath, CancellationToken cancellationToken)
	{
		try
		{
			var options = new CopilotClientOptions
			{
				LogLevel = CopilotLogLevel.Error,
				UseLoggedInUser = true,
				Connection = CopilotSdkProvider.BuildRuntimeConnection(executablePath)
			};

			// The page waits on this probe, so a CLI that never answers must not hang it.
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(15));

			await using var client = new CopilotClient(options);
			var authStatus = await client.GetAuthStatusAsync(timeout.Token);
			if (authStatus is null || !authStatus.IsAuthenticated)
			{
				return null;
			}

			var typeLabel = MapCopilotAuthTypeLabel(authStatus.AuthType);
			return new AuthenticationState(true, BuildCopilotAuthenticationMessage(typeLabel, authStatus.Login), typeLabel);
		}
		catch
		{
			return null;
		}
	}

	private static string BuildCopilotAuthenticationMessage(string? typeLabel, string? login)
	{
		if (!string.IsNullOrWhiteSpace(login))
		{
			return $"{typeLabel ?? "Copilot"} authentication detected for {login}.";
		}

		return $"{typeLabel ?? "Copilot"} authentication detected on host.";
	}

	private static string? MapCopilotAuthTypeLabel(string? authType) => authType?.Trim().ToLowerInvariant() switch
	{
		"user" => "Logged-in User",
		"env" => "Environment Token",
		"gh-cli" => "GitHub CLI",
		"hmac" => "HMAC",
		"api-key" => "API Key",
		"token" => "GitHub Token",
		_ => string.IsNullOrWhiteSpace(authType) ? null : authType
	};

	internal static bool TryReadCopilotAuthStatus()
	{
		var configPath = Path.Combine(GetUserHomeDirectory(), ".copilot", "config.json");
		if (!File.Exists(configPath))
		{
			return false;
		}

		try
		{
			// Copilot CLI 1.x writes "//" header comments and camelCase keys; older builds wrote plain
			// JSON with snake_case keys. Accept both.
			using var stream = File.OpenRead(configPath);
			using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
			{
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true
			});
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			string[] loginProperties = ["lastLoggedInUser", "loggedInUsers", "last_logged_in_user", "logged_in_users", "copilot_tokens"];
			return loginProperties.Any(propertyName => HasNonEmptyObjectOrArray(document.RootElement, propertyName));
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

	internal sealed record InstallSpec(string ExecutableName, string Label, string Command, string VersionArguments);
	internal sealed record AuthenticationState(bool IsAuthenticated, string? Message, string? TypeLabel);
	internal sealed record ClaudeLoginStatus(bool LoggedIn, DateTime? ExpiresAt);
	private sealed record ProcessResult(bool Success, string? Output, string? ErrorMessage);
	private sealed record ShellCommand(string FileName, IReadOnlyList<string> Arguments);
}
