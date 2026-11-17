using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// A named group of projects, such as the repositories that make up one product.
/// Folders only organise the projects list: projects in the same folder are not linked.
/// </summary>
public class ProjectFolder
{
	public Guid Id { get; set; }

	[Required(ErrorMessage = "Folder name is required.")]
	[StringLength(ValidationLimits.ProjectFolderNameMaxLength, MinimumLength = 1, ErrorMessage = "Folder name is required.")]
	public string Name { get; set; } = string.Empty;

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? UpdatedAt { get; set; }
}
