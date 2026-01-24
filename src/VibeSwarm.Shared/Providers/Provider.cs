using System.ComponentModel.DataAnnotations;

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastConnectedAt { get; set; }
}
