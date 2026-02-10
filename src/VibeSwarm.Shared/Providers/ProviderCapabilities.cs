namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Defines the capabilities and supported connection modes for each provider type.
/// Claude and Copilot support CLI and SDK modes. OpenCode supports CLI and REST.
/// </summary>
public static class ProviderCapabilities
{
	/// <summary>
	/// Gets the supported connection modes for a provider type.
	/// </summary>
	public static IReadOnlyList<ProviderConnectionMode> GetSupportedModes(ProviderType providerType) => providerType switch
	{
		ProviderType.OpenCode => [ProviderConnectionMode.CLI, ProviderConnectionMode.REST],
		ProviderType.Claude => [ProviderConnectionMode.CLI, ProviderConnectionMode.SDK],
		ProviderType.Copilot => [ProviderConnectionMode.CLI, ProviderConnectionMode.SDK],
		_ => [ProviderConnectionMode.CLI]
	};

	/// <summary>
	/// Gets the default connection mode for a provider type.
	/// </summary>
	public static ProviderConnectionMode GetDefaultMode(ProviderType providerType) => providerType switch
	{
		ProviderType.OpenCode => ProviderConnectionMode.REST,
		ProviderType.Claude => ProviderConnectionMode.CLI,
		ProviderType.Copilot => ProviderConnectionMode.CLI,
		_ => ProviderConnectionMode.CLI
	};

	/// <summary>
	/// Checks if a provider type supports a specific connection mode.
	/// </summary>
	public static bool SupportsMode(ProviderType providerType, ProviderConnectionMode mode)
		=> GetSupportedModes(providerType).Contains(mode);

	/// <summary>
	/// Gets a user-friendly description for a provider type.
	/// </summary>
	public static string GetDescription(ProviderType providerType) => providerType switch
	{
		ProviderType.OpenCode => "OpenCode AI agent with REST API and CLI support",
		ProviderType.Claude => "Anthropic Claude Code with CLI and SDK support",
		ProviderType.Copilot => "GitHub Copilot with CLI and SDK support",
		_ => "Unknown provider"
	};

	/// <summary>
	/// Gets the default executable name for a provider type.
	/// </summary>
	public static string GetDefaultExecutable(ProviderType providerType) => providerType switch
	{
		ProviderType.OpenCode => "opencode",
		ProviderType.Claude => "claude",
		ProviderType.Copilot => "copilot",
		_ => ""
	};

	/// <summary>
	/// Gets a brief description of the connection mode for a provider type.
	/// </summary>
	public static string GetModeDescription(ProviderType providerType, ProviderConnectionMode mode) => (providerType, mode) switch
	{
		(ProviderType.Copilot, ProviderConnectionMode.SDK) => "Uses the GitHub.Copilot.SDK NuGet package for programmatic control via JSON-RPC. Requires the Copilot CLI installed.",
		(ProviderType.Copilot, ProviderConnectionMode.CLI) => "Spawns the Copilot CLI process directly for each job execution.",
		(ProviderType.Claude, ProviderConnectionMode.SDK) => "Uses the Anthropic .NET SDK for direct API access. Requires an Anthropic API key.",
		(ProviderType.Claude, ProviderConnectionMode.CLI) => "Spawns the Claude Code CLI process directly for each job execution.",
		(ProviderType.OpenCode, ProviderConnectionMode.REST) => "Connects to the OpenCode REST API server.",
		(ProviderType.OpenCode, ProviderConnectionMode.CLI) => "Spawns the OpenCode CLI process directly for each job execution.",
		_ => $"{mode} mode for {providerType}."
	};

	/// <summary>
	/// Returns whether the "Update CLI" action is applicable for a given mode.
	/// </summary>
	public static bool SupportsCliUpdate(ProviderConnectionMode mode) => mode == ProviderConnectionMode.CLI;

	/// <summary>
	/// Validates provider configuration and returns validation errors.
	/// </summary>
	public static IReadOnlyList<string> ValidateConfiguration(Provider provider)
	{
		var errors = new List<string>();

		// Validate connection mode is supported
		if (!SupportsMode(provider.Type, provider.ConnectionMode))
		{
			errors.Add($"{provider.Type} does not support {provider.ConnectionMode} connection mode.");
		}

		// Validate REST mode requirements
		if (provider.ConnectionMode == ProviderConnectionMode.REST)
		{
			if (string.IsNullOrWhiteSpace(provider.ApiEndpoint))
			{
				errors.Add("API Endpoint is required for REST connection mode.");
			}
			else if (!Uri.TryCreate(provider.ApiEndpoint, UriKind.Absolute, out var uri) ||
					 (uri.Scheme != "http" && uri.Scheme != "https"))
			{
				errors.Add("API Endpoint must be a valid HTTP or HTTPS URL.");
			}
		}

		// Validate SDK mode requirements
		if (provider.ConnectionMode == ProviderConnectionMode.SDK)
		{
			if (provider.Type == ProviderType.Claude && string.IsNullOrWhiteSpace(provider.ApiKey))
			{
				errors.Add("API Key is required for Claude SDK connection mode.");
			}
		}

		return errors;
	}
}
