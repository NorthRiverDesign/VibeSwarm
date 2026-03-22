using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class DashboardProjectOrderingTests
{
	[Fact]
	public void Sort_LastRan_PrioritizesMostRecentJobThenProjectName()
	{
		var nowUtc = DateTime.UtcNow;
		var projects = new[]
		{
			CreateProjectInfo("Zulu", nowUtc.AddHours(-6)),
			CreateProjectInfo("Bravo"),
			CreateProjectInfo("Alpha", nowUtc.AddHours(-1)),
			CreateProjectInfo("Charlie")
		};

		var orderedNames = DashboardProjectOrdering.Sort(projects, DashboardProjectSortOption.LastRan)
			.Select(project => project.Project.Name)
			.ToList();

		Assert.Equal(["Alpha", "Zulu", "Bravo", "Charlie"], orderedNames);
	}

	[Fact]
	public void Sort_Name_OrdersAlphabeticallyAndUsesLastRunAsTieBreaker()
	{
		var nowUtc = DateTime.UtcNow;
		var projects = new[]
		{
			CreateProjectInfo("beta", nowUtc.AddHours(-8)),
			CreateProjectInfo("Alpha", nowUtc.AddHours(-1)),
			CreateProjectInfo("charlie"),
			CreateProjectInfo("Beta", nowUtc.AddHours(-2))
		};

		var orderedNames = DashboardProjectOrdering.Sort(projects, DashboardProjectSortOption.Name)
			.Select(project => project.Project.Name)
			.ToList();

		Assert.Equal(["Alpha", "Beta", "beta", "charlie"], orderedNames);
	}

	private static DashboardProjectInfo CreateProjectInfo(string name, DateTime? lastRanAt = null)
	{
		return new DashboardProjectInfo
		{
			Project = new Project
			{
				Id = Guid.NewGuid(),
				Name = name,
				WorkingPath = $"/tmp/{name.ToLowerInvariant()}"
			},
			LatestJob = lastRanAt.HasValue
				? new JobSummary
				{
					Id = Guid.NewGuid(),
					ProjectId = Guid.NewGuid(),
					ProviderId = Guid.NewGuid(),
					GoalPrompt = $"{name} latest job",
					Status = JobStatus.Completed,
					CreatedAt = lastRanAt.Value
				}
				: null
		};
	}
}
