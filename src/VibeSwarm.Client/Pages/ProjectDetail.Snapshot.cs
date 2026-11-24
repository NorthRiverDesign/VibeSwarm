using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Client.Pages;

public partial class ProjectDetail
{
	/// <summary>
	/// What the page showed when last left, as it opens (first pages, no job search or filter), and
	/// drawn at once on the next visit while everything reloads. Git state is left out: it moves
	/// with every job, and the branch actions must never act on an old reading.
	/// </summary>
	private sealed record ProjectDetailSnapshot(
		Project Project,
		List<JobSummary> Jobs,
		int JobsTotalCount,
		int ActiveJobsCount,
		int CompletedJobsCount,
		JobSummary? ActiveJobSummary,
		List<Idea> Ideas,
		int IdeasTotalCount,
		int IdeasUnprocessedCount,
		bool IsIdeasProcessingActive,
		List<Provider> Providers,
		IterationLoop? AutoPilotStatus);

	private ViewSnapshotCache? _snapshotCache;
	private bool _projectDeleted;

	private string SnapshotKey => $"project:{ProjectId}";

	/// <summary>Fills the page from its last visit, if there was one. Returns whether it did.</summary>
	private bool RestoreSnapshot()
	{
		_snapshotCache = ServiceProvider.GetService<ViewSnapshotCache>();
		if (_snapshotCache is null || !_snapshotCache.TryGet<ProjectDetailSnapshot>(SnapshotKey, out var snapshot))
		{
			return false;
		}

		Project = snapshot.Project;
		PagedJobs = snapshot.Jobs;
		JobsTotalCount = snapshot.JobsTotalCount;
		ProjectActiveJobsCount = snapshot.ActiveJobsCount;
		ProjectCompletedJobsCount = snapshot.CompletedJobsCount;
		_activeJobSummary = snapshot.ActiveJobSummary;
		Ideas = snapshot.Ideas;
		IdeasTotalCount = snapshot.IdeasTotalCount;
		IdeasUnprocessedCount = snapshot.IdeasUnprocessedCount;
		IsIdeasProcessingActive = snapshot.IsIdeasProcessingActive;
		Providers = snapshot.Providers;
		_autoPilotStatus = snapshot.AutoPilotStatus;
		IsLoading = false;
		return true;
	}

	private void SaveSnapshot()
	{
		// Another page or a search would be the wrong thing to open on, and a list still loading
		// may belong to the view just left; the last saved first-page view stays instead.
		var isOpeningView = _jobsPageNumber == 1 && _ideasPageNumber == 1
			&& string.IsNullOrEmpty(_jobSearchQuery) && _jobStatusFilter == "all";
		var isSettled = !_isLoadingJobs && !_isLoadingIdeas && !_isLoadingJobsPage && !_isLoadingIdeasPage;
		if (_snapshotCache is null || Project is null || Project.Id != ProjectId || _projectDeleted || !isOpeningView || !isSettled)
		{
			return;
		}

		_snapshotCache.Set(SnapshotKey, new ProjectDetailSnapshot(Project, PagedJobs, JobsTotalCount,
			ProjectActiveJobsCount, ProjectCompletedJobsCount, _activeJobSummary, Ideas, IdeasTotalCount,
			IdeasUnprocessedCount, IsIdeasProcessingActive, Providers, _autoPilotStatus));
	}

	private void ForgetSnapshot()
	{
		_projectDeleted = true;
		_snapshotCache?.Remove(SnapshotKey);
	}
}
