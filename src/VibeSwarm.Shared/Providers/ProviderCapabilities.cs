namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Defines the capabilities and supported connection modes for each provider type.
/// Only OpenCode currently supports REST API, other providers are CLI-only.
/// </summary>
public static class ProviderCapabilities
{
	/// <summary>
	/// Gets the supported connection modes for a provider type.
	/// </summary>
	public static IReadOnlyList<ProviderConnectionMode> GetSupportedModes(ProviderType providerType) => providerType switch
	{
		ProviderType.OpenCode => [ProviderConnectionMode.CLI, ProviderConnectionMode.REST],
		ProviderType.Claude => [ProviderConnectionMode.CLI],
		ProviderType.Copilot => [ProviderConnectionMode.CLI],
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
		ProviderType.Claude => "Anthropic Claude Code CLI agent",
		ProviderType.Copilot => "GitHub Copilot CLI agent",
		_ => "Unknown provider"
	};

	/// <summary>
	/// Gets the default executable name for a provider type.
	/// </summary>
	public static string GetDefaultExecutable(ProviderType providerType) => providerType switch
	{
		ProviderType.OpenCode => "opencode",
		ProviderType.Claude => "claude",
		ProviderType.Copilot => "github-copilot-cli",
		_ => ""
	};

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

		return errors;
	}
}
