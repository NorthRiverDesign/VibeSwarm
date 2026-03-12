using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

public enum EnvironmentType
{
	Web = 0,
	GitHubReleases = 1,
	Generic = 2
}

public class ProjectEnvironment
{
	public Guid Id { get; set; }

	public Guid ProjectId { get; set; }

	[JsonIgnore]
	public Project? Project { get; set; }

	[Required]
	[StringLength(100, MinimumLength = 1)]
	public string Name { get; set; } = string.Empty;

	[StringLength(1000)]
	public string? Notes { get; set; }

	[Required]
	[StringLength(1000, MinimumLength = 1)]
	[Url]
	public string Url { get; set; } = string.Empty;

	public EnvironmentType Type { get; set; } = EnvironmentType.Web;

	public bool IsDefault { get; set; }

	public bool IsEnabled { get; set; } = true;

	public int SortOrder { get; set; }

	[NotMapped]
	[StringLength(200)]
	public string? Username { get; set; }

	[NotMapped]
	[StringLength(500)]
	public string? Password { get; set; }

	[NotMapped]
	public bool ClearPassword { get; set; }

	[JsonIgnore]
	[StringLength(4000)]
	public string? EncryptedUsername { get; set; }

	[JsonIgnore]
	[StringLength(4000)]
	public string? EncryptedPassword { get; set; }

	[NotMapped]
	public bool HasPassword { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }
}
