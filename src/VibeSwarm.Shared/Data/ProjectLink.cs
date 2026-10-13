using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Links one project to another. Links go both ways, so each one is stored as a pair of rows,
/// and jobs on either project are told about the other.
/// </summary>
public class ProjectLink
{
	public Guid ProjectId { get; set; }

	public Guid LinkedProjectId { get; set; }

	[JsonIgnore]
	public Project? LinkedProject { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
