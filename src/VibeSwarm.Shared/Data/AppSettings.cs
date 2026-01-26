using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Represents application-wide settings stored in the database.
/// This is a single-row table that stores global configuration.
/// </summary>
public class AppSettings
{
	public Guid Id { get; set; }

	/// <summary>
	/// The default directory where new projects will be created.
	/// This path is automatically prefixed when creating a new Project.
	/// </summary>
	[StringLength(1000)]
	public string? DefaultProjectsDirectory { get; set; }

	/// <summary>
	/// When the settings were last updated.
	/// </summary>
	public DateTime? UpdatedAt { get; set; }
}
