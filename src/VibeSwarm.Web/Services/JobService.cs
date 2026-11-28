using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Shared.VersionControl;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Services;

public partial class JobService : IJobService
{
    private const int DefaultJobsPageSize = 25;
    private const int DefaultProjectJobsPageSize = 10;
    private const int MaxPageSize = 100;
    private const int JobDetailMessageLimit = 250;

    private readonly VibeSwarmDbContext _dbContext;
    private readonly DbContextOptions<VibeSwarmDbContext>? _dbContextOptions;
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
        _dbContextOptions = serviceProvider.GetService<DbContextOptions<VibeSwarmDbContext>>();
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
			Priority = j.Priority,
			CreatedAt = j.CreatedAt,
			StartedAt = j.StartedAt,
			CompletedAt = j.CompletedAt,
			ExecutionDurationSeconds = j.Statistics != null ? j.Statistics.ExecutionDurationSeconds : null,
			TotalCostUsd = j.Statistics != null ? j.Statistics.TotalCostUsd : null,
			InputTokens = j.Statistics != null ? j.Statistics.InputTokens : null,
			OutputTokens = j.Statistics != null ? j.Statistics.OutputTokens : null,
			IsTokenEstimate = j.Statistics != null && j.Statistics.IsTokenEstimate,
			PlanningCostUsd = j.PlanningStatistics != null ? j.PlanningStatistics.CostUsd : null,
			PlanningInputTokens = j.PlanningStatistics != null ? j.PlanningStatistics.InputTokens : null,
			PlanningOutputTokens = j.PlanningStatistics != null ? j.PlanningStatistics.OutputTokens : null,
			ExecutionCostUsd = j.ExecutionStatistics != null ? j.ExecutionStatistics.CostUsd : null,
			ExecutionInputTokens = j.ExecutionStatistics != null ? j.ExecutionStatistics.InputTokens : null,
			ExecutionOutputTokens = j.ExecutionStatistics != null ? j.ExecutionStatistics.OutputTokens : null,
             Branch = j.Branch,
             ChangedFilesCount = j.ChangedFilesCount,
             BuildVerified = j.BuildVerified,
             ReviewStatus = j.ReviewStatus,
             BuildVerificationEnabled = j.Project != null && j.Project.BuildVerificationEnabled,
             GitCommitHash = j.GitCommitHash,
             PullRequestNumber = j.PullRequestNumber,
             PullRequestUrl = j.PullRequestUrl,
             MergedAt = j.MergedAt,
             IsPushed = !string.IsNullOrWhiteSpace(j.PullRequestUrl)
             	|| j.MergedAt.HasValue
             	|| (!string.IsNullOrWhiteSpace(j.GitCommitHash)
             		&& (j.GitChangeDeliveryMode == GitChangeDeliveryMode.PullRequest
             			|| j.CommitModeOverride == AutoCommitMode.CommitAndPush
             			|| (j.CommitModeOverride == null && j.Project != null && j.Project.AutoCommitMode == AutoCommitMode.CommitAndPush))),
             SessionSummary = j.SessionSummary,
             IsScheduled = j.IsScheduled,
            JobScheduleId = j.JobScheduleId,
            PlaywrightEnabled = j.PlaywrightEnabled,
            EnvironmentCount = j.EnvironmentCount,
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

        // Aggregate counts and token totals always reflect the full project, not the filtered view
        var activeCount = await projectQuery.CountAsync(j =>
            j.Status == JobStatus.New ||
            j.Status == JobStatus.Pending ||
            j.Status == JobStatus.Started ||
            j.Status == JobStatus.Planning ||
            j.Status == JobStatus.Processing ||
            j.Status == JobStatus.Paused, cancellationToken);

        var completedCount = await projectQuery.CountAsync(j =>
            j.Status == JobStatus.Completed, cancellationToken);

        var tokenAggregates = await projectQuery
            .Select(j => new
            {
                Input = j.InputTokens ?? 0,
                Output = j.OutputTokens ?? 0,
                Cost = j.TotalCostUsd ?? 0m,
            })
            .ToListAsync(cancellationToken);

        var totalInputTokens = tokenAggregates.Sum(a => a.Input);
        var totalOutputTokens = tokenAggregates.Sum(a => a.Output);
        var totalCostUsd = tokenAggregates.Sum(a => a.Cost);

        // Fetch the first active/processing job summary for the header
        var activeJobSummary = await ProjectToSummary(
                projectQuery.Where(j =>
                    j.Status == JobStatus.Started ||
                    j.Status == JobStatus.Planning ||
                    j.Status == JobStatus.Processing)
                .OrderBy(j => j.StartedAt ?? j.CreatedAt)
                .Take(1))
            .FirstOrDefaultAsync(cancellationToken);

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
            TotalInputTokens = totalInputTokens,
            TotalOutputTokens = totalOutputTokens,
            TotalCostUsd = totalCostUsd,
            ActiveJobSummary = activeJobSummary,
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

        // A project runs one job at a time: every job resets and cleans the shared checkout
        // before it starts. Members of one team swarm are the exception, each running in a
        // worktree of its own (see ProjectJobSlots). A provider runs up to its own job limit,
        // each job in a different project (see ProviderJobSlots).
        var runningJobInfo = await _dbContext.Jobs
            .Where(j => j.Status == JobStatus.Pending
                || j.Status == JobStatus.Started
                || j.Status == JobStatus.Planning
                || j.Status == JobStatus.Processing
                || j.Status == JobStatus.Paused
                || j.Status == JobStatus.Stalled)
            .Select(j => new { j.ProjectId, j.ProviderId, j.SwarmId })
            .ToListAsync(cancellationToken);

        var projectSlots = new ProjectJobSlots(
            runningJobInfo.Select(j => (j.ProjectId, j.SwarmId)),
            await ProjectCheckouts.LoadAsync(_dbContext, cancellationToken));
        var closedProjects = projectSlots.ClosedProjectIds;
        var providerSlots = new ProviderJobSlots(
            runningJobInfo.Select(j => j.ProviderId),
            await ProviderJobSlots.LoadLimitsAsync(_dbContext, cancellationToken));
        var fullProviders = providerSlots.FullProviderIds;

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
            .Where(j => !closedProjects.Contains(j.ProjectId))
            .Where(j => j.ProviderId == Guid.Empty || !fullProviders.Contains(j.ProviderId))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .ToListAsync(cancellationToken);

        // Return at most one job per project unless they belong to one swarm, so the worker
        // never launches two concurrent runs against the same checkout, and no more jobs per
        // provider than it has room for. The worker caps the total (AppSettings.MaxConcurrentJobs).
        var projectHolds = _serviceProvider.GetService<ProjectQueueHolds>();
        var result = new List<Job>();
        foreach (var job in pendingJobs)
        {
            // Held projects (a queued job is being edited) wait without blocking anyone else.
            if (projectHolds?.IsHeld(job.ProjectId) == true)
            {
                continue;
            }

            if (!projectSlots.CanStart(job.ProjectId, job.SwarmId))
            {
                continue;
            }

            if (!providerSlots.CanStart(job.ProviderId))
            {
                continue;
            }

            projectSlots.Take(job.ProjectId, job.SwarmId);
            providerSlots.Take(job.ProviderId);
            result.Add(job);
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
		var job = await IncludeStatistics(_dbContext.Jobs)
			.Include(j => j.Project)
				.ThenInclude(p => p!.Environments)
			.Include(j => j.Provider)
			.Include(j => j.PlanningProvider)
			.Include(j => j.ProviderAttempts.OrderBy(a => a.AttemptOrder))
			.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
		if (job == null)
		{
			return null;
		}

		job.TotalMessageCount = await _dbContext.Set<JobMessage>()
			.Where(message => message.JobId == id)
			.CountAsync(cancellationToken);

		job.Messages = await _dbContext.Set<JobMessage>()
			.Where(message => message.JobId == id)
			.OrderByDescending(message => message.CreatedAt)
			.Take(JobDetailMessageLimit)
			.OrderBy(message => message.CreatedAt)
			.ToListAsync(cancellationToken);

		return job;
    }

	public Task<JobLiveOutput?> GetLiveOutputAsync(Guid id, long afterSequence = 0, CancellationToken cancellationToken = default)
		=> Task.FromResult(_jobProcessingService?.GetLiveOutput(id, afterSequence));

    public async Task<Job> CreateAsync(Job job, CancellationToken cancellationToken = default)
    {
        var requestedInstances = Math.Clamp(job.RequestedInstances, 1, ValidationLimits.JobInstancesMax);
        job.RequestedInstances = 1;
        NormalizeJobForPersistence(job);
        await ApplyJobProfileAsync(job, cancellationToken);
        NormalizeJobForPersistence(job);
        await ValidateRequestedExecutionAsync(job, cancellationToken);

        // Drop deserialized navigation graphs so EF doesn't try to re-track stub entities
        // that may collide with already-tracked instances loaded during validation above.
        job.Project = null;
        job.Provider = null;
        job.PlanningProvider = null;
        job.JobSchedule = null;
        job.JobTemplate = null;
        job.Statistics = null;
        job.PlanningStatistics = null;
        job.ExecutionStatistics = null;
        job.Messages = new List<JobMessage>();
        job.ChangeSets = new List<JobChangeSet>();

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

        // A merge has one resolution and a setup job one result, so neither is split into instances.
        // Copies split a goal written as a list, never running more copies than it has items:
        // copies left to divide prose among themselves would overlap.
        var canRunAsInstances = !LocalEnvironmentSetup.IsSetupJob(job) && string.IsNullOrWhiteSpace(job.MergeFromBranch);
        var instanceCount = canRunAsInstances && requestedInstances > 1
            ? Math.Min(requestedInstances, JobInstanceSplitter.GetListItems(job.GoalPrompt).Count)
            : 1;
        // Decided once, here: the project's team works the job unless it is a merge, a setup job
        // or one of several copies, which each have a single agent's work to do.
        job.TeamRun = !job.RunSolo && canRunAsInstances && instanceCount == 1
            && await _dbContext.Projects.AsNoTracking()
                .Where(project => project.Id == job.ProjectId)
                .Select(project => project.TeamEnabled)
                .FirstOrDefaultAsync(cancellationToken);
        job.PlanSkippedReason = null;
        job.LeadChecks = 0;

        var instanceJobs = instanceCount > 1
            ? BuildInstanceJobs(job, instanceCount)
            : [];

        // Persist via a brand-new DbContext constructed from the same options as _dbContext.
        // This guarantees an empty change tracker at the moment of Add, regardless of what
        // the request-scoped _dbContext (or callers like IdeaService) already have attached.
        // A job's instances are saved with it, so none can start before the others exist.
        if (_dbContextOptions is not null)
        {
            await using var freshContext = new VibeSwarmDbContext(_dbContextOptions);
            freshContext.Jobs.Add(job);
            freshContext.Jobs.AddRange(instanceJobs);
            await freshContext.SaveChangesAsync(cancellationToken);
            freshContext.Entry(job).State = EntityState.Detached;
            foreach (var instanceJob in instanceJobs)
            {
                freshContext.Entry(instanceJob).State = EntityState.Detached;
            }
        }
        else
        {
            // Tests that construct JobService with an empty IServiceProvider land here.
            _dbContext.Jobs.Add(job);
            _dbContext.Jobs.AddRange(instanceJobs);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // A template's use count is the number of jobs it has queued, counted here so every way of
        // creating one (the job form, a template run) counts the same. Child jobs are part of their
        // parent's run.
        if (job.JobTemplateId is Guid templateId && job.ParentJobId == null)
        {
            await _dbContext.JobTemplates
                .Where(template => template.Id == templateId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(template => template.UseCount, template => template.UseCount + 1), cancellationToken);
        }

		var swarmJobs = instanceJobs;

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

    /// <summary>
    /// Makes the job the first of <paramref name="count"/> instances and builds the rest: copies
    /// on the same provider, model and plan that share a swarm, so each runs in a worktree of its
    /// own at the same time and its work is replayed onto the branch as it finishes.
    /// </summary>
    private static List<Job> BuildInstanceJobs(Job primaryJob, int count)
    {
        var swarmId = Guid.NewGuid();
        var baseTitle = primaryJob.Title;
        primaryJob.SwarmId = swarmId;
        primaryJob.InstanceNumber = 1;
        primaryJob.Title = BuildInstanceTitle(baseTitle, 1, count);

        var instances = new List<Job>(count - 1);
        for (var number = 2; number <= count; number++)
        {
            instances.Add(new Job
            {
                Id = Guid.NewGuid(),
                Title = BuildInstanceTitle(baseTitle, number, count),
                GoalPrompt = primaryJob.GoalPrompt,
                Status = JobStatus.New,
                ProjectId = primaryJob.ProjectId,
                ProviderId = primaryJob.ProviderId,
                ModelUsed = primaryJob.ModelUsed,
                ReasoningEffort = primaryJob.ReasoningEffort,
                ProfileId = primaryJob.ProfileId,
                ProfileName = primaryJob.ProfileName,
                ExecutionPlan = primaryJob.ExecutionPlan,
                ActiveExecutionIndex = 0,
                Branch = primaryJob.Branch,
                TargetBranch = primaryJob.TargetBranch,
                GitChangeDeliveryMode = primaryJob.GitChangeDeliveryMode,
                CommitModeOverride = primaryJob.CommitModeOverride,
                Priority = primaryJob.Priority,
                NotBeforeUtc = primaryJob.NotBeforeUtc,
                MaxCostUsd = primaryJob.MaxCostUsd,
                MaxExecutionMinutes = primaryJob.MaxExecutionMinutes,
                MaxTokens = primaryJob.MaxTokens,
                CreatedAt = primaryJob.CreatedAt,
                SwarmId = swarmId,
                InstanceNumber = number,
            });
        }

        return instances;
    }

    private static string BuildInstanceTitle(string? title, int number, int count)
    {
        var suffix = $" ({number}/{count})";
        var baseTitle = string.IsNullOrWhiteSpace(title) ? "Job" : title.Trim();
        const int maxTitleLength = 200;
        if (baseTitle.Length + suffix.Length > maxTitleLength)
        {
            baseTitle = baseTitle[..(maxTitleLength - suffix.Length - 1)].TrimEnd() + "…";
        }

        return baseTitle + suffix;
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
            // Everything not yet finished, including jobs waiting on the user or stuck,
            // so the jobs page can show what is running and what comes next.
            "current" => query.Where(job =>
                job.Status == JobStatus.New ||
                job.Status == JobStatus.Pending ||
                job.Status == JobStatus.Started ||
                job.Status == JobStatus.Planning ||
                job.Status == JobStatus.Processing ||
                job.Status == JobStatus.Paused ||
                job.Status == JobStatus.Stalled),
            "finished" => query.Where(job =>
                job.Status == JobStatus.Completed ||
                job.Status == JobStatus.Failed ||
                job.Status == JobStatus.Cancelled),
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
            "current" => "current",
            "finished" => "finished",
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
