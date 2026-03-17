using VibeSwarm.Shared.Services;

namespace VibeSwarm.Client.Models;

public enum DashboardProjectSortOption
{
	LastRan,
	Name
}

public static class DashboardProjectOrdering
{
	public static IReadOnlyList<DashboardProjectInfo> Sort(
		IEnumerable<DashboardProjectInfo> projects,
		DashboardProjectSortOption sortOption)
	{
		ArgumentNullException.ThrowIfNull(projects);

		return sortOption switch
		{
			DashboardProjectSortOption.Name => projects
				.OrderBy(project => project.Project.Name, StringComparer.OrdinalIgnoreCase)
				.ThenByDescending(GetLastRanAt)
				.ToList(),
			_ => projects
				.OrderByDescending(project => GetLastRanAt(project).HasValue)
				.ThenByDescending(GetLastRanAt)
				.ThenBy(project => project.Project.Name, StringComparer.OrdinalIgnoreCase)
				.ToList()
		};
	}

	private static DateTime? GetLastRanAt(DashboardProjectInfo project)
	{
		return project.LatestJob?.CreatedAt;
	}
}
