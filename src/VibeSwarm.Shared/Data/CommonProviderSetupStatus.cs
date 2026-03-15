using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

public class CommonProviderSetupStatus
{
	public ProviderType ProviderType { get; set; }
	public string DisplayName { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string DocumentationUrl { get; set; } = string.Empty;
	public string ExecutableName { get; set; } = string.Empty;
	public string InstallMethodLabel { get; set; } = string.Empty;
	public string InstallCommand { get; set; } = string.Empty;
	public string ApiKeyLabel { get; set; } = "API Key";
	public string ApiKeyPlaceholder { get; set; } = string.Empty;
	public string ApiKeyHelpText { get; set; } = string.Empty;
	public bool IsInstalled { get; set; }
	public string? InstalledVersion { get; set; }
	public bool HasConfiguredProvider { get; set; }
	public Guid? ProviderId { get; set; }
	public string? ProviderName { get; set; }
	public bool IsAuthenticated { get; set; }
	public string? AuthenticationStatus { get; set; }
}

public class CommonProviderSetupRequest
{
	[Required]
	public ProviderType ProviderType { get; set; }

	[Required]
	[StringLength(200, MinimumLength = 1)]
	public string ApiKey { get; set; } = string.Empty;
}

public class CommonProviderActionResult
{
	public bool Success { get; set; }
	public string Message { get; set; } = string.Empty;
	public string? ErrorMessage { get; set; }
	public CommonProviderSetupStatus? Status { get; set; }
}
