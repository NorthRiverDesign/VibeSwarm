using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Web.Services;

public sealed class ProjectReleaseService : IProjectReleaseService
{
	private const int ListedChanges = 30;

	/// <summary>Ahead of queued ideas: more work landing on development first could conflict too.</summary>
	internal const int ConflictJobPriority = 1;

	private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> DeployLocks = new();

	private static readonly JobStatus[] ActiveStatuses =
		[JobStatus.New, JobStatus.Pending, JobStatus.Started, JobStatus.Planning, JobStatus.Processing, JobStatus.Paused, JobStatus.Stalled];

	private readonly VibeSwarmDbContext _dbContext;
	private readonly IVersionControlService _versionControl;
	private readonly ReleaseBranchGit _releaseGit;
	private readonly IJobService _jobService;
	private readonly ILogger<ProjectReleaseService> _logger;

	public ProjectReleaseService(
		VibeSwarmDbContext dbContext,
		IVersionControlService versionControl,
		ReleaseBranchGit releaseGit,
		IJobService jobService,
		ILogger<ProjectReleaseService> logger)
	{
		_dbContext = dbContext;
		_versionControl = versionControl;
		_releaseGit = releaseGit;
		_jobService = jobService;
		_logger = logger;
	}

	public async Task<ProjectReleaseStatus> GetStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var project = await LoadProjectAsync(projectId, cancellationToken);
		var status = new ProjectReleaseStatus
		{
			DevelopmentBranch = project.DevelopmentBranch,
			ReleaseBranch = project.ReleaseBranch
		};
		if (!status.IsConfigured)
		{
			return status;
		}

		status.ResolvingJobId = await FindResolvingJobAsync(project, cancellationToken);
		if (!await _versionControl.IsGitRepositoryAsync(project.WorkingPath, cancellationToken))
		{
			status.Error = "The project folder isn't a git repository.";
			return status;
		}

		var fetch = await _versionControl.FetchAsync(project.WorkingPath, cancellationToken: cancellationToken);
		if (!fetch.Success)
		{
			_logger.LogWarning("Couldn't fetch {Project} for its release status: {Error}", project.Name, fetch.Error);
		}

		var developmentRef = await _releaseGit.ResolveRefAsync(project.WorkingPath, project.DevelopmentBranch!, cancellationToken);
		var releaseRef = await _releaseGit.ResolveRefAsync(project.WorkingPath, project.ReleaseBranch!, cancellationToken);
		if (developmentRef == null || releaseRef == null)
		{
			status.Error = $"The branch '{(developmentRef == null ? project.DevelopmentBranch : project.ReleaseBranch)}' doesn't exist yet.";
			return status;
		}

		(status.PendingCount, status.PendingChanges) = await _releaseGit.GetChangesAsync(
			project.WorkingPath, releaseRef, developmentRef, ListedChanges, cancellationToken);
		status.ReleaseHead = await _releaseGit.GetCommitAsync(project.WorkingPath, releaseRef, cancellationToken);
		return status;
	}

	public async Task<DeployResult> DeployAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		var deployLock = DeployLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
		if (!await deployLock.WaitAsync(0, cancellationToken))
		{
			return Fail("A deploy is already running for this project.");
		}

		try
		{
			var project = await LoadProjectAsync(projectId, cancellationToken);
			if (string.IsNullOrWhiteSpace(project.DevelopmentBranch) || string.IsNullOrWhiteSpace(project.ReleaseBranch))
			{
				return Fail("Set a development branch and a release branch in the project's settings first.");
			}

			var development = project.DevelopmentBranch.Trim();
			var release = project.ReleaseBranch.Trim();
			var resolvingJobId = await FindResolvingJobAsync(project, cancellationToken);
			if (resolvingJobId != null)
			{
				return new DeployResult
				{
					Outcome = DeployOutcome.ResolvingConflicts,
					Message = "A provider is still resolving the conflicts. Deploy again once its job finishes.",
					JobId = resolvingJobId
				};
			}

			_logger.LogInformation("Deploying {Development} to {Release} for {Project}", development, release, project.Name);
			var merge = await _versionControl.MergeBranchAsync(
				project.WorkingPath,
				development,
				release,
				cancellationToken: cancellationToken,
				pushAfterMerge: true,
				preferRemoteSource: true);

			if (merge.Success)
			{
				return merge.ChangedFilesCount == 0
					? new DeployResult { Outcome = DeployOutcome.NothingToDeploy, Message = $"'{release}' already has everything on '{development}'." }
					: new DeployResult { Outcome = DeployOutcome.Deployed, Message = $"Deployed '{development}' to '{release}'.", CommitHash = merge.CommitHash };
			}

			if (merge.MergeConflictFiles.Count == 0)
			{
				_logger.LogWarning("Deploy of {Project} failed: {Error}", project.Name, merge.Error);
				return Fail(merge.Error ?? "The merge failed.");
			}

			var conflictedFiles = merge.MergeConflictFiles.Select(file => file.FileName).ToList();
			var job = await QueueConflictJobAsync(project, development, release, conflictedFiles, cancellationToken);
			_logger.LogInformation("Deploy of {Project} conflicted in {Count} file(s); queued job {JobId} to resolve them",
				project.Name, conflictedFiles.Count, job.Id);
			return new DeployResult
			{
				Outcome = DeployOutcome.ResolvingConflicts,
				Message = $"'{release}' has changes that conflict with '{development}' in {conflictedFiles.Count} file(s). " +
					"Nothing was deployed; a provider is resolving them on the development branch. Deploy again once its job finishes.",
				JobId = job.Id
			};
		}
		finally
		{
			deployLock.Release();
		}
	}

	private Task<Job> QueueConflictJobAsync(Project project, string development, string release, IReadOnlyList<string> conflictedFiles, CancellationToken cancellationToken)
	{
		var fileList = string.Join(Environment.NewLine, conflictedFiles.Take(30).Select(file => $"- {file}"));
		return _jobService.CreateAsync(new Job
		{
			ProjectId = project.Id,
			Title = $"Resolve conflicts to deploy {development} to {release}",
			GoalPrompt =
				$"Deploying '{development}' to '{release}' hit merge conflicts, so nothing was deployed. " +
				$"Merge '{release}' into '{development}' and resolve the conflicts so the next deploy goes through cleanly. " +
				$"VibeSwarm starts the merge before you begin. The deploy conflicted in:{Environment.NewLine}{fileList}",
			Branch = development,
			MergeFromBranch = release,
			GitChangeDeliveryMode = GitChangeDeliveryMode.CommitToBranch,
			CommitModeOverride = AutoCommitMode.CommitAndPush,
			Priority = ConflictJobPriority
		}, cancellationToken);
	}

	private async Task<Guid?> FindResolvingJobAsync(Project project, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(project.ReleaseBranch))
		{
			return null;
		}

		var release = project.ReleaseBranch.Trim();
		return await _dbContext.Jobs
			.AsNoTracking()
			.Where(job => job.ProjectId == project.Id && job.MergeFromBranch == release && ActiveStatuses.Contains(job.Status))
			.OrderByDescending(job => job.CreatedAt)
			.Select(job => (Guid?)job.Id)
			.FirstOrDefaultAsync(cancellationToken);
	}

	private async Task<Project> LoadProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
		await _dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
			?? throw new KeyNotFoundException("Project not found.");

	private static DeployResult Fail(string message) => new() { Outcome = DeployOutcome.Failed, Message = message };
}
