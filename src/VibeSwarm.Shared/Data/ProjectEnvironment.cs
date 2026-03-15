using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

public enum EnvironmentType
{
	Web = 0,
	Release = 1,
	Other = 2
}

public enum EnvironmentStage
{
	Production = 0,
	Development = 1,
	Local = 2
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
	public string? Description { get; set; }

	[Required]
	[StringLength(1000, MinimumLength = 1)]
	[Url]
	public string Url { get; set; } = string.Empty;

	public EnvironmentType Type { get; set; } = EnvironmentType.Web;

	public EnvironmentStage Stage { get; set; } = EnvironmentStage.Production;

	public bool IsPrimary { get; set; }

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
	public string? UsernameCiphertext { get; set; }

	[JsonIgnore]
	[StringLength(4000)]
	public string? PasswordCiphertext { get; set; }

	[NotMapped]
	public bool HasPassword => !string.IsNullOrEmpty(PasswordCiphertext) || !string.IsNullOrEmpty(Password);

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }
}
