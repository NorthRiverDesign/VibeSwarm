using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

/// <summary>
/// The branch a job works on when nothing names one: the project's development branch, or failing
/// that whatever the checkout is on. Ideas and templates both queue jobs this way.
/// </summary>
internal static class ProjectJobBranch
{
	public static async Task<string?> ResolveDefaultAsync(
		Project project,
		IVersionControlService versionControlService,
		ILogger logger,
		CancellationToken cancellationToken = default)
	{
		if (!string.IsNullOrWhiteSpace(project.DevelopmentBranch))
		{
			return project.DevelopmentBranch;
		}

		try
		{
			if (await versionControlService.IsGitRepositoryAsync(project.WorkingPath, cancellationToken))
			{
				return await versionControlService.GetCurrentBranchAsync(project.WorkingPath, cancellationToken);
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not get current branch for project {ProjectId}", project.Id);
		}

		return null;
	}
}
