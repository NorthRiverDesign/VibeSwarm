using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Providers;

public class Provider
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public ProviderType Type { get; set; }

    [Required]
    public ProviderConnectionMode ConnectionMode { get; set; } = ProviderConnectionMode.CLI;

    [StringLength(500)]
    public string? ExecutablePath { get; set; }

    [StringLength(500)]
    public string? WorkingDirectory { get; set; }

    [StringLength(500)]
    [Url]
    public string? ApiEndpoint { get; set; }

    [StringLength(200)]
    public string? ApiKey { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool IsDefault { get; set; } = false;

    /// <summary>
    /// Maximum execution time for jobs using this provider (in minutes).
    /// Jobs exceeding this time will be automatically terminated.
    /// Null means no provider-level limit (uses job or system defaults).
    /// </summary>
    [Range(1, 10080)] // 1 minute to 7 days
    public int? MaxExecutionMinutes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastConnectedAt { get; set; }

    /// <summary>
    /// When the available models list was last refreshed from the provider
    /// </summary>
    public DateTime? LastModelsRefreshAt { get; set; }

    /// <summary>
    /// Available AI models for this provider
    /// </summary>
    public ICollection<ProviderModel> AvailableModels { get; set; } = new List<ProviderModel>();
}
