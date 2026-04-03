using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

public partial class JobService : IJobService
{
    private const int DefaultJobsPageSize = 25;
    private const int DefaultProjectJobsPageSize = 10;
    private const int MaxPageSize = 100;

    private readonly VibeSwarmDbContext _dbContext;
    private readonly IJobUpdateService? _jobUpdateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly JobProcessingService? _jobProcessingService;

    public JobService(
        VibeSwarmDbContext dbContext,
        IServiceProvider serviceProvider,
        IJobUpdateService? jobUpdateService = null,
        JobProcessingService? jobProcessingService = null)
    {
        _dbContext = dbContext;
        _serviceProvider = serviceProvider;
        _jobUpdateService = jobUpdateService;
        _jobProcessingService = jobProcessingService;
    }

    /// <summary>
    /// Projects a Job query into lightweight JobSummary DTOs, excluding heavy text fields.
    /// </summary>
	internal static IQueryable<JobSummary> ProjectToSummary(IQueryable<Job> query)
	{
		return query.Select(j => new JobSummary
		{
            Id = j.Id,
            Title = j.Title,
            GoalPrompt = j.GoalPrompt,
            Status = j.Status,
            ProjectId = j.ProjectId,
            ProjectName = j.Project != null ? j.Project.Name : null,
            ProviderId = j.ProviderId,
            ProviderName = j.Provider != null ? j.Provider.Name : null,
            ModelUsed = j.ModelUsed,
            PlanningProviderId = j.PlanningProviderId,
            PlanningProviderName = j.PlanningProvider != null ? j.PlanningProvider.Name : null,
            PlanningModelUsed = j.PlanningModelUsed,
			CurrentActivity = j.CurrentActivity,
			ErrorMessage = j.ErrorMessage,
			CreatedAt = j.CreatedAt,
			StartedAt = j.StartedAt,
			CompletedAt = j.CompletedAt,
			ExecutionDurationSeconds = j.Statistics != null ? j.Statistics.ExecutionDurationSeconds : null,
			TotalCostUsd = j.Statistics != null ? j.Statistics.TotalCostUsd : null,
			InputTokens = j.Statistics != null ? j.Statistics.InputTokens : null,
			OutputTokens = j.Statistics != null ? j.Statistics.OutputTokens : null,
			PlanningCostUsd = j.PlanningStatistics != null ? j.PlanningStatistics.CostUsd : null,
			PlanningInputTokens = j.PlanningStatistics != null ? j.PlanningStatistics.InputTokens : null,
			PlanningOutputTokens = j.PlanningStatistics != null ? j.PlanningStatistics.OutputTokens : null,
			ExecutionCostUsd = j.ExecutionStatistics != null ? j.ExecutionStatistics.CostUsd : null,
			ExecutionInputTokens = j.ExecutionStatistics != null ? j.ExecutionStatistics.InputTokens : null,
			ExecutionOutputTokens = j.ExecutionStatistics != null ? j.ExecutionStatistics.OutputTokens : null,
            CurrentCycle = j.CurrentCycle,
            MaxCycles = j.MaxCycles,
            CycleMode = j.CycleMode,
            TeamRoleName = j.TeamRole != null ? j.TeamRole.Name : null,
            Branch = j.Branch,
            ChangedFilesCount = j.ChangedFilesCount,
            BuildVerified = j.BuildVerified,
            GitCommitHash = j.GitCommitHash,
            PullRequestNumber = j.PullRequestNumber,
            PullRequestUrl = j.PullRequestUrl,
            IsScheduled = j.IsScheduled,
            JobScheduleId = j.JobScheduleId,
        });
    }

	public async Task<IEnumerable<Job>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await IncludeStatistics(_dbContext.Jobs)
			.Include(j => j.Project)
				.ThenInclude(p => p!.Environments)
			.Include(j => j.Provider)
            .Include(j => j.PlanningProvider)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<JobsListResult> GetPagedAsync(Guid? projectId = null, string statusFilter = "all", int page = 1, int pageSize = DefaultJobsPageSize, CancellationToken cancellationToken = default)
    {
        var normalizedPageSize = NormalizePageSize(pageSize, DefaultJobsPageSize);
        var filteredQuery = BuildFilteredJobsQuery(projectId, statusFilter);
        var totalCount = await filteredQuery.CountAsync(cancellationToken);
        var normalizedPage = NormalizePageNumber(page, normalizedPageSize, totalCount);

		var aggregates = totalCount == 0
			? null
			: await filteredQuery
				.GroupBy(_ => 1)
				.Select(group => new
				{
					TotalInputTokens = group.Sum(job => job.Statistics != null ? job.Statistics.InputTokens ?? 0 : 0),
					TotalOutputTokens = group.Sum(job => job.Statistics != null ? job.Statistics.OutputTokens ?? 0 : 0),
					TotalCostUsd = group.Sum(job => job.Statistics != null ? job.Statistics.TotalCostUsd ?? 0m : 0m)
				})
				.FirstOrDefaultAsync(cancellationToken);

        return new JobsListResult
        {
            PageNumber = normalizedPage,
            PageSize = normalizedPageSize,
            TotalCount = totalCount,
            TotalInputTokens = aggregates?.TotalInputTokens ?? 0,
            TotalOutputTokens = aggregates?.TotalOutputTokens ?? 0,
            TotalCostUsd = aggregates?.TotalCostUsd ?? 0m,
            ProjectCounts = await _dbContext.Jobs
                .GroupBy(job => job.ProjectId)
                .Select(group => new JobProjectCountSummary
                {
                    ProjectId = group.Key,
                    TotalCount = group.Count(),
                     ActiveCount = group.Count(job =>
                         job.Status == JobStatus.New ||
                         job.Status == JobStatus.Started ||
                         job.Status == JobStatus.Planning ||
                         job.Status == JobStatus.Processing)
                 })
                .ToListAsync(cancellationToken),
            Items = await ProjectToSummary(
                    filteredQuery
                        .OrderByDescending(j => j.CreatedAt)
                        .Skip((normalizedPage - 1) * normalizedPageSize)
                        .Take(normalizedPageSize))
                .ToListAsync(cancellationToken)
        };
    }

	public async Task<IEnumerable<Job>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default)
	{
		return await IncludeStatistics(_dbContext.Jobs)
			.Include(j => j.Provider)
			.Where(j => j.ProjectId == projectId)
			.OrderByDescending(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ProjectJobsListResult> GetPagedByProjectIdAsync(Guid projectId, int page = 1, int pageSize = DefaultProjectJobsPageSize, string? search = null, string statusFilter = "all", CancellationToken cancellationToken = default)
    {
        var normalizedPageSize = NormalizePageSize(pageSize, DefaultProjectJobsPageSize);
        var projectQuery = _dbContext.Jobs
            .Where(j => j.ProjectId == projectId);

        // Aggregate counts always reflect the full project, not the filtered view
        var activeCount = await projectQuery.CountAsync(j =>
            j.Status == JobStatus.New ||
            j.Status == JobStatus.Pending ||
            j.Status == JobStatus.Started ||
            j.Status == JobStatus.Planning ||
            j.Status == JobStatus.Processing ||
            j.Status == JobStatus.Paused, cancellationToken);
        var completedCount = await projectQuery.CountAsync(j => j.Status == JobStatus.Completed, cancellationToken);

        var baseQuery = projectQuery.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            baseQuery = baseQuery.Where(j =>
                (j.Title != null && j.Title.ToLower().Contains(term)) ||
                j.GoalPrompt.ToLower().Contains(term));
        }

        if (statusFilter != "all")
        {
            baseQuery = statusFilter switch
            {
                "active" => baseQuery.Where(j =>
                    j.Status == JobStatus.New ||
                    j.Status == JobStatus.Pending ||
                    j.Status == JobStatus.Started ||
                    j.Status == JobStatus.Planning ||
                    j.Status == JobStatus.Processing ||
                    j.Status == JobStatus.Paused ||
                    j.Status == JobStatus.Stalled),
                "completed" => baseQuery.Where(j => j.Status == JobStatus.Completed),
                "failed" => baseQuery.Where(j => j.Status == JobStatus.Failed),
                "cancelled" => baseQuery.Where(j => j.Status == JobStatus.Cancelled),
                _ => baseQuery
            };
        }

        var totalCount = await baseQuery.CountAsync(cancellationToken);
        var normalizedPage = NormalizePageNumber(page, normalizedPageSize, totalCount);

        return new ProjectJobsListResult
        {
            PageNumber = normalizedPage,
            PageSize = normalizedPageSize,
            TotalCount = totalCount,
            ActiveCount = activeCount,
            CompletedCount = completedCount,
            Items = await ProjectToSummary(
                    baseQuery
                        .OrderByDescending(j => j.CreatedAt)
                        .Skip((normalizedPage - 1) * normalizedPageSize)
                        .Take(normalizedPageSize))
                .ToListAsync(cancellationToken)
        };
    }

	public async Task<IEnumerable<Job>> GetPendingJobsAsync(CancellationToken cancellationToken = default)
	{
		var now = DateTime.UtcNow;

        // Get the SwarmId (if any) of currently running jobs, grouped by project.
        // If ALL running jobs for a project share the same SwarmId, that swarm's remaining
        // members are allowed to start concurrently (swarm-aware scheduling).
        var runningJobInfo = await _dbContext.Jobs
            .Where(j => j.Status == JobStatus.Pending
                || j.Status == JobStatus.Started
                || j.Status == JobStatus.Planning
                || j.Status == JobStatus.Processing
                || j.Status == JobStatus.Paused
                || j.Status == JobStatus.Stalled)
            .Select(j => new { j.ProjectId, j.SwarmId })
            .ToListAsync(cancellationToken);

        var projectsWithRunningJobs = runningJobInfo.Select(j => j.ProjectId).Distinct().ToList();

        // For each project, determine if the running jobs all belong to the same swarm.
        // If so, pending jobs from that same swarm can proceed.
        // Collect the swarm IDs that are actively running (one per swarm-active project).
        // Using a List<Guid> so EF Core can translate Contains() to SQL IN (...).
        var activeSwarmIds = runningJobInfo
            .GroupBy(j => j.ProjectId)
            .Where(g => g.All(j => j.SwarmId.HasValue)
                && g.Select(j => j.SwarmId).Distinct().Count() == 1)
            .Select(g => g.First().SwarmId!.Value)
            .ToList();

		var pendingJobs = await _dbContext.Jobs
			.Include(j => j.Statistics)
			.Include(j => j.PlanningStatistics)
			.Include(j => j.ExecutionStatistics)
			.Include(j => j.Project)
				.ThenInclude(p => p!.Environments)
			.Include(j => j.Provider)
            .Where(j => j.Status == JobStatus.New && !j.CancellationRequested)
			.Where(j => j.NotBeforeUtc == null || j.NotBeforeUtc <= now)
			.Where(j => !j.DependsOnJobId.HasValue
				|| _dbContext.Jobs.Any(dependency => dependency.Id == j.DependsOnJobId && dependency.Status == JobStatus.Completed))
            .Where(j => !projectsWithRunningJobs.Contains(j.ProjectId)
                || (j.SwarmId != null && activeSwarmIds.Contains(j.SwarmId.Value)))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .ToListAsync(cancellationToken);

        // Return all swarm member jobs that are eligible, but only one non-swarm job per project.
        var seen = new HashSet<Guid>();
        var result = new List<Job>();
        foreach (var job in pendingJobs)
        {
            if (job.SwarmId.HasValue)
            {
                result.Add(job); // All swarm members can be dispatched together
            }
            else if (seen.Add(job.ProjectId))
            {
                result.Add(job); // One non-swarm job per project
            }
        }

        return result;
    }

    public async Task<IEnumerable<JobSummary>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        return await ProjectToSummary(
                _dbContext.Jobs
                    .Where(j => j.Status == JobStatus.Started || j.Status == JobStatus.Planning || j.Status == JobStatus.Processing || j.Status == JobStatus.New)
                    .OrderByDescending(j => j.StartedAt ?? j.CreatedAt))
            .ToListAsync(cancellationToken);
    }

	public async Task<Job?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await IncludeStatistics(_dbContext.Jobs)
			.Include(j => j.Project)
				.ThenInclude(p => p!.Environments)
			.Include(j => j.Provider)
            .Include(j => j.PlanningProvider)
            .Include(j => j.ProviderAttempts.OrderBy(a => a.AttemptOrder))
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

	public async Task<Job?> GetByIdWithMessagesAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await IncludeStatistics(_dbContext.Jobs)
			.Include(j => j.Project)
				.ThenInclude(p => p!.Environments)
			.Include(j => j.Provider)
            .Include(j => j.PlanningProvider)
            .Include(j => j.ProviderAttempts.OrderBy(a => a.AttemptOrder))
            .Include(j => j.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default)
    {
        NormalizeJobForPersistence(job);
        await ValidateRequestedExecutionAsync(job, cancellationToken);

        job.Id = Guid.NewGuid();
        job.CreatedAt = DateTime.UtcNow;
        job.Status = JobStatus.New;
        job.ActiveExecutionIndex = 0;
        job.LastSwitchAt = null;
        job.LastSwitchReason = null;
        job.ProviderAttempts.Clear();

        await InitializeExecutionPlanAsync(job, cancellationToken);
		if (job.ProviderId == Guid.Empty)
		{
			throw new InvalidOperationException("No enabled providers are available for this job.");
		}

        _dbContext.Jobs.Add(job);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Fan out to team swarm jobs if the project has team swarm enabled
		var swarmJobs = job.TeamRoleId.HasValue
			? []
			: await TryCreateTeamSwarmJobsAsync(job, cancellationToken);

        // Notify that a new job was created (so processing can start immediately)
        if (_jobUpdateService != null)
        {
            try
            {
                await _jobUpdateService.NotifyJobCreated(job.Id, job.ProjectId);
                await _jobUpdateService.NotifyJobStatusChanged(job.Id, job.Status.ToString());

                foreach (var swarmJob in swarmJobs)
                {
                    await _jobUpdateService.NotifyJobCreated(swarmJob.Id, swarmJob.ProjectId);
                    await _jobUpdateService.NotifyJobStatusChanged(swarmJob.Id, swarmJob.Status.ToString());
                }
            }
            catch
            {
                // Don't fail job creation if notification fails
            }
        }

        _jobProcessingService?.TriggerProcessing();

        return job;
    }

    private async Task<List<Job>> TryCreateTeamSwarmJobsAsync(Job primaryJob, CancellationToken cancellationToken)
    {
        var project = await _dbContext.Projects
            .Include(p => p.TeamAssignments)
                .ThenInclude(a => a.TeamRole)
            .Include(p => p.TeamAssignments)
                .ThenInclude(a => a.Provider)
            .FirstOrDefaultAsync(p => p.Id == primaryJob.ProjectId, cancellationToken);

        if (project == null || !project.EnableTeamSwarm)
            return [];

        var enabledAssignments = project.TeamAssignments
            .Where(a => a.IsEnabled
                && a.TeamRole != null
                && a.TeamRole.IsEnabled
                && a.ProviderId != Guid.Empty)
            .OrderBy(a => a.TeamRole!.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (enabledAssignments.Count < 2)
            return [];

        var swarmId = Guid.NewGuid();
        var createdJobs = new List<Job>();

        // Assign the first role to the primary job
        var primaryJobDb = await _dbContext.Jobs.FindAsync(new object[] { primaryJob.Id }, cancellationToken);
        if (primaryJobDb != null)
        {
            var firstAssignment = enabledAssignments[0];
            primaryJobDb.SwarmId = swarmId;
            primaryJobDb.TeamRoleId = firstAssignment.TeamRoleId;
			primaryJobDb.ProviderId = firstAssignment.ProviderId;
            if (!string.IsNullOrWhiteSpace(firstAssignment.PreferredModelId))
            {
                primaryJobDb.ModelUsed = firstAssignment.PreferredModelId;
            }
            primaryJobDb.ReasoningEffort = firstAssignment.PreferredReasoningEffort;
            // Update the in-memory job so callers see the changes
            primaryJob.SwarmId = swarmId;
            primaryJob.TeamRoleId = firstAssignment.TeamRoleId;
            primaryJob.ReasoningEffort = firstAssignment.PreferredReasoningEffort;
        }

        // Create sibling jobs for each remaining role
        for (var i = 1; i < enabledAssignments.Count; i++)
        {
            var assignment = enabledAssignments[i];
            var roleJob = new Job
            {
                Id = Guid.NewGuid(),
                Title = primaryJob.Title,
                GoalPrompt = primaryJob.GoalPrompt,
                Status = JobStatus.New,
                ProjectId = primaryJob.ProjectId,
				ProviderId = assignment.ProviderId,
                ModelUsed = string.IsNullOrWhiteSpace(assignment.PreferredModelId) ? null : assignment.PreferredModelId,
                ReasoningEffort = assignment.PreferredReasoningEffort,
                Branch = primaryJob.Branch,
                TargetBranch = primaryJob.TargetBranch,
                GitChangeDeliveryMode = primaryJob.GitChangeDeliveryMode,
                Priority = primaryJob.Priority,
                CreatedAt = DateTime.UtcNow,
                MaxCostUsd = primaryJob.MaxCostUsd,
                MaxExecutionMinutes = primaryJob.MaxExecutionMinutes,
                MaxTokens = primaryJob.MaxTokens,
                SwarmId = swarmId,
                TeamRoleId = assignment.TeamRoleId,
            };

            await InitializeExecutionPlanAsync(roleJob, cancellationToken);
            // Ensure the role's provider is used even if execution plan defaulted to another
            if (roleJob.ProviderId == Guid.Empty || roleJob.ProviderId != assignment.ProviderId)
            {
                roleJob.ProviderId = assignment.ProviderId;
                if (!string.IsNullOrWhiteSpace(assignment.PreferredModelId))
                    roleJob.ModelUsed = assignment.PreferredModelId;
                roleJob.ReasoningEffort = assignment.PreferredReasoningEffort;
            }

            _dbContext.Jobs.Add(roleJob);
            createdJobs.Add(roleJob);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return createdJobs;
    }

	private IQueryable<Job> BuildFilteredJobsQuery(Guid? projectId, string statusFilter)
    {
        var query = _dbContext.Jobs.AsQueryable();

        if (projectId.HasValue)
        {
            query = query.Where(job => job.ProjectId == projectId.Value);
        }

        return NormalizeStatusFilter(statusFilter) switch
        {
            "active" => query.Where(job =>
                job.Status == JobStatus.New ||
                job.Status == JobStatus.Started ||
                job.Status == JobStatus.Planning ||
                job.Status == JobStatus.Processing),
            "completed" => query.Where(job => job.Status == JobStatus.Completed),
            "failed" => query.Where(job =>
                job.Status == JobStatus.Failed ||
                job.Status == JobStatus.Cancelled),
            _ => query
        };
    }

    private static string NormalizeStatusFilter(string? statusFilter)
    {
        if (string.IsNullOrWhiteSpace(statusFilter))
        {
            return "all";
        }

        return statusFilter.Trim().ToLowerInvariant() switch
        {
            "active" => "active",
            "completed" => "completed",
            "failed" => "failed",
            _ => "all"
        };
    }

	private static void NormalizeJobForPersistence(Job job)
    {
        job.GoalPrompt = job.GoalPrompt?.Trim() ?? string.Empty;
        job.Title = JobTitleHelper.BuildSafeJobTitle(job.Title, job.GoalPrompt);
        job.JobTemplateId = job.JobTemplateId == Guid.Empty ? null : job.JobTemplateId;
        job.ModelUsed = string.IsNullOrWhiteSpace(job.ModelUsed) ? null : job.ModelUsed.Trim();
        job.ReasoningEffort = ProviderCapabilities.NormalizeReasoningEffort(job.ReasoningEffort);
        job.PlanningOutput = string.IsNullOrWhiteSpace(job.PlanningOutput) ? null : job.PlanningOutput.Trim();
        job.PlanningModelUsed = string.IsNullOrWhiteSpace(job.PlanningModelUsed) ? null : job.PlanningModelUsed.Trim();
        job.PlanningReasoningEffortUsed = ProviderCapabilities.NormalizeReasoningEffort(job.PlanningReasoningEffortUsed);
        job.Branch = string.IsNullOrWhiteSpace(job.Branch) ? null : job.Branch.Trim();
		job.TargetBranch = string.IsNullOrWhiteSpace(job.TargetBranch) ? null : job.TargetBranch.Trim();
	}

	private static IQueryable<Job> IncludeStatistics(IQueryable<Job> query)
	{
		return query
			.Include(job => job.Statistics)
			.Include(job => job.PlanningStatistics)
			.Include(job => job.ExecutionStatistics);
	}

    private static int NormalizePageSize(int pageSize, int defaultPageSize)
    {
        if (pageSize <= 0)
        {
            return defaultPageSize;
        }

        return Math.Min(pageSize, MaxPageSize);
    }

    private static int NormalizePageNumber(int pageNumber, int pageSize, int totalCount)
    {
        if (totalCount <= 0)
        {
            return 1;
        }

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Math.Min(Math.Max(pageNumber, 1), totalPages);
    }
}
