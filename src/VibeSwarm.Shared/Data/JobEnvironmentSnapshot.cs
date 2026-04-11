namespace VibeSwarm.Shared.Data;

/// <summary>
/// Lightweight snapshot of a project environment captured at job execution time.
/// Stored in Job.EnvironmentsJson for audit and display — no credentials included.
/// </summary>
public class JobEnvironmentSnapshot
{
	public string Name { get; set; } = string.Empty;
	public string Url { get; set; } = string.Empty;
	public EnvironmentType Type { get; set; }
	public EnvironmentStage Stage { get; set; }
	public bool IsPrimary { get; set; }

	/// <summary>
	/// Creates a snapshot list from the project's enabled environments.
	/// </summary>
	public static List<JobEnvironmentSnapshot> FromProject(Project? project)
	{
		if (project?.Environments == null || project.Environments.Count == 0)
		{
			return new List<JobEnvironmentSnapshot>();
		}

		return project.Environments
			.Where(e => e.IsEnabled)
			.OrderByDescending(e => e.IsPrimary)
			.ThenBy(e => e.SortOrder)
			.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
			.Select(e => new JobEnvironmentSnapshot
			{
				Name = e.Name,
				Url = e.Url,
				Type = e.Type,
				Stage = e.Stage,
				IsPrimary = e.IsPrimary
			})
			.ToList();
	}
}
