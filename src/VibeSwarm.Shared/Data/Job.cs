using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Specifies how cycles are managed for a job.
/// </summary>
public enum CycleMode
{
    /// <summary>
    /// Single cycle only (default behavior).
    /// </summary>
    SingleCycle = 0,

    /// <summary>
    /// Fixed number of cycles. Job runs exactly MaxCycles times.
    /// </summary>
    FixedCount = 1,

    /// <summary>
    /// Autonomous mode. Agent decides when to stop by responding with CYCLE_COMPLETE.
    /// </summary>
    Autonomous = 2
}

/// <summary>
/// Specifies how sessions are handled between cycles.
/// </summary>
public enum CycleSessionMode
{
    /// <summary>
    /// Continue using the same session across cycles.
    /// </summary>
    ContinueSession = 0,

    /// <summary>
    /// Start a fresh session for each cycle.
    /// </summary>
    FreshSession = 1
}

/// <summary>
/// Tracks whether VibeSwarm preserved local git changes before a destructive operation.
/// </summary>
public enum GitCheckpointStatus
{
    None = 0,
    Protecting = 1,
    Preserved = 2,
    Cleared = 3
}

public class Job
{
	private Guid _id;

    public Guid Id
	{
		get => _id;
		set
		{
			_id = value;
			SyncStatisticsKeys();
		}
	}

    /// <summary>
    /// Short display title for the job. For manual jobs, this is derived from the goal prompt.
    /// For jobs created from ideas, this is the original idea text.
    /// </summary>
    [StringLength(200)]
    public string? Title { get; set; }

    /// <summary>
    /// Short display text for previews and compact lists.
    /// Uses the original idea text when available, otherwise falls back to the goal prompt.
    /// </summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? GoalPrompt : Title;

    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string GoalPrompt { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of absolute attachment paths supplied with this job execution.
    /// </summary>
    public string? AttachedFilesJson { get; set; }

    public JobStatus Status { get; set; } = JobStatus.New;

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

	public Guid ProviderId { get; set; }
    public Provider? Provider { get; set; }

	public bool IsScheduled { get; set; }
	public Guid? JobScheduleId { get; set; }
	public JobSchedule? JobSchedule { get; set; }
	public Guid? JobTemplateId { get; set; }
	public JobTemplate? JobTemplate { get; set; }
	public DateTime? ScheduledForUtc { get; set; }

    /// <summary>
    /// The AI model that was used to execute this job (e.g., "claude-sonnet-4-20250514")
    /// </summary>
    [StringLength(200)]
    public string? ModelUsed { get; set; }

    /// <summary>
    /// The requested reasoning effort for this job.
    /// </summary>
    [StringLength(VibeSwarm.Shared.Validation.ValidationLimits.ReasoningEffortMaxLength)]
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Planning output generated before execution when project planning is enabled.
    /// Persisted so execution and retries can reuse the reviewed plan without regenerating it.
    /// </summary>
    public string? PlanningOutput { get; set; }

	/// <summary>
	/// Provider that generated the persisted planning output, if any.
	/// </summary>
	public Guid? PlanningProviderId { get; set; }
	public Provider? PlanningProvider { get; set; }

    /// <summary>
    /// Model that generated the persisted planning output, if any.
    /// </summary>
    [StringLength(200)]
    public string? PlanningModelUsed { get; set; }

    /// <summary>
    /// Reasoning effort used to generate the persisted planning output, if any.
    /// </summary>
    [StringLength(VibeSwarm.Shared.Validation.ValidationLimits.ReasoningEffortMaxLength)]
    public string? PlanningReasoningEffortUsed { get; set; }

	/// <summary>
	/// When the persisted planning output was last generated.
	/// </summary>
	public DateTime? PlanningGeneratedAt { get; set; }

	/// <summary>
	/// Input tokens consumed while generating the persisted planning output.
	/// </summary>
	[NotMapped]
	public int? PlanningInputTokens
	{
		get => PlanningStatistics?.InputTokens;
		set
		{
			if (value is null && PlanningStatistics is null)
			{
				return;
			}

			EnsurePlanningStatistics().InputTokens = value;
		}
	}

	/// <summary>
	/// Output tokens consumed while generating the persisted planning output.
	/// </summary>
	[NotMapped]
	public int? PlanningOutputTokens
	{
		get => PlanningStatistics?.OutputTokens;
		set
		{
			if (value is null && PlanningStatistics is null)
			{
				return;
			}

			EnsurePlanningStatistics().OutputTokens = value;
		}
	}

	/// <summary>
	/// Cost in USD for generating the persisted planning output.
	/// </summary>
	[NotMapped]
	public decimal? PlanningCostUsd
	{
		get => PlanningStatistics?.CostUsd;
		set
		{
			if (value is null && PlanningStatistics is null)
			{
				return;
			}

			EnsurePlanningStatistics().CostUsd = value;
		}
	}

    /// <summary>
    /// Ordered provider-model execution plan captured when the job is scheduled or reset.
    /// Stored as JSON so retries can resume deterministic fallback.
    /// </summary>
    public string? ExecutionPlan { get; set; }

    /// <summary>
    /// Index of the currently active provider-model candidate within ExecutionPlan.
    /// </summary>
    public int ActiveExecutionIndex { get; set; }

    /// <summary>
    /// Last reason the job switched providers or models.
    /// </summary>
    [StringLength(200)]
    public string? LastSwitchReason { get; set; }

    /// <summary>
    /// When the job last switched providers or models.
    /// </summary>
    public DateTime? LastSwitchAt { get; set; }

    /// <summary>
    /// The git branch this job should operate within.
    /// If specified, the worker will switch to this branch before starting the job.
    /// </summary>
    [StringLength(250)]
    public string? Branch { get; set; }

	/// <summary>
	/// Controls how this job's changes should be delivered after completion.
	/// </summary>
	public GitChangeDeliveryMode GitChangeDeliveryMode { get; set; } = GitChangeDeliveryMode.CommitToBranch;

	/// <summary>
	/// Optional target branch used when creating a pull request or merging changes.
	/// </summary>
	[StringLength(250)]
	public string? TargetBranch { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Total execution time in seconds. Stored explicitly to preserve duration
    /// even if StartedAt/CompletedAt are modified or unavailable.
    /// </summary>
    [NotMapped]
    public double? ExecutionDurationSeconds
	{
		get => Statistics?.ExecutionDurationSeconds;
		set
		{
			if (value is null && Statistics is null)
			{
				return;
			}

			EnsureStatistics().ExecutionDurationSeconds = value;
		}
	}

    /// <summary>
    /// Gets the execution duration as a TimeSpan.
    /// Returns the explicit duration if stored, otherwise calculates from timestamps.
    /// </summary>
    public TimeSpan? ExecutionDuration
    {
        get
        {
            if (ExecutionDurationSeconds.HasValue)
                return TimeSpan.FromSeconds(ExecutionDurationSeconds.Value);
            if (StartedAt.HasValue && CompletedAt.HasValue)
                return CompletedAt.Value - StartedAt.Value;
            if (StartedAt.HasValue && (Status == JobStatus.Started || Status == JobStatus.Planning || Status == JobStatus.Processing))
                return DateTime.UtcNow - StartedAt.Value;
            return null;
        }
    }

    public string? Output { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Session ID from the provider (e.g., Claude session UUID)
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Flag to request cancellation of the job
    /// </summary>
    public bool CancellationRequested { get; set; }

    /// <summary>
    /// Total cost in USD for this job (if available from provider)
    /// </summary>
    [NotMapped]
    public decimal? TotalCostUsd
	{
		get => Statistics?.TotalCostUsd;
		set
		{
			if (value is null && Statistics is null)
			{
				return;
			}

			EnsureStatistics().TotalCostUsd = value;
		}
	}

    /// <summary>
    /// Total input tokens used
    /// </summary>
    [NotMapped]
    public int? InputTokens
	{
		get => Statistics?.InputTokens;
		set
		{
			if (value is null && Statistics is null)
			{
				return;
			}

			EnsureStatistics().InputTokens = value;
		}
	}

	/// <summary>
	/// Total output tokens used
	/// </summary>
	[NotMapped]
	public int? OutputTokens
	{
		get => Statistics?.OutputTokens;
		set
		{
			if (value is null && Statistics is null)
			{
				return;
			}

			EnsureStatistics().OutputTokens = value;
		}
	}

	/// <summary>
	/// True when InputTokens/OutputTokens are estimates derived from text length
	/// rather than exact counts reported by the provider.
	/// </summary>
	[NotMapped]
	public bool IsTokenEstimate
	{
		get => Statistics?.IsTokenEstimate ?? false;
		set
		{
			if (!value && Statistics is null)
			{
				return;
			}

			EnsureStatistics().IsTokenEstimate = value;
		}
	}

	/// <summary>
	/// Input tokens consumed by the execution stage.
	/// </summary>
	[NotMapped]
	public int? ExecutionInputTokens
	{
		get => ExecutionStatistics?.InputTokens;
		set
		{
			if (value is null && ExecutionStatistics is null)
			{
				return;
			}

			EnsureExecutionStatistics().InputTokens = value;
		}
	}

	/// <summary>
	/// Output tokens consumed by the execution stage.
	/// </summary>
	[NotMapped]
	public int? ExecutionOutputTokens
	{
		get => ExecutionStatistics?.OutputTokens;
		set
		{
			if (value is null && ExecutionStatistics is null)
			{
				return;
			}

			EnsureExecutionStatistics().OutputTokens = value;
		}
	}

	/// <summary>
	/// Cost in USD for the execution stage.
	/// </summary>
	[NotMapped]
	public decimal? ExecutionCostUsd
	{
		get => ExecutionStatistics?.CostUsd;
		set
		{
			if (value is null && ExecutionStatistics is null)
			{
				return;
			}

			EnsureExecutionStatistics().CostUsd = value;
		}
	}

    /// <summary>
    /// Current activity description (e.g., "Running tool: Read", "Thinking...")
    /// </summary>
    public string? CurrentActivity { get; set; }

    /// <summary>
    /// Last time the job was updated with progress
    /// </summary>
    public DateTime? LastActivityAt { get; set; }

    /// <summary>
    /// The worker instance ID that is currently processing this job.
    /// Used for detecting orphaned jobs when a worker crashes.
    /// </summary>
    public string? WorkerInstanceId { get; set; }

    /// <summary>
    /// Last heartbeat from the worker processing this job.
    /// Used to detect stalled jobs.
    /// </summary>
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>
    /// Number of times this job has been retried
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Maximum number of retry attempts allowed (0 = no limit)
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Process ID of the CLI process (for force cancellation)
    /// </summary>
    public int? ProcessId { get; set; }

    /// <summary>
    /// The exact CLI command used to execute this job (for debugging and transparency)
    /// </summary>
    [StringLength(4000)]
    public string? CommandUsed { get; set; }

    /// <summary>
    /// The exact CLI command used during the planning stage, when present.
    /// </summary>
    [StringLength(4000)]
    public string? PlanningCommandUsed { get; set; }

    /// <summary>
    /// The exact CLI command used during the execution stage, when present.
    /// </summary>
    [StringLength(4000)]
    public string? ExecutionCommandUsed { get; set; }

    /// <summary>
    /// Priority level for job scheduling (higher = more urgent)
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Maximum execution time before the job is considered timed out (in minutes)
    /// </summary>
    public int? MaxExecutionMinutes { get; set; }

    /// <summary>
    /// Maximum cost in USD before stopping the job
    /// </summary>
    public decimal? MaxCostUsd { get; set; }

    /// <summary>
    /// Maximum tokens (input + output) before stopping the job
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Time without activity before job is considered stalled (in seconds)
    /// </summary>
    public int? StallTimeoutSeconds { get; set; }

    /// <summary>
    /// Regex pattern that indicates success when found in output
    /// </summary>
    public string? SuccessPattern { get; set; }

    /// <summary>
    /// Regex pattern that indicates failure when found in output or error
    /// </summary>
    public string? FailurePattern { get; set; }

    /// <summary>
    /// Tags for categorizing and filtering jobs
    /// </summary>
    public string? Tags { get; set; }

    /// <summary>
    /// Parent job ID for job chaining/dependencies
    /// </summary>
    public Guid? ParentJobId { get; set; }

    /// <summary>
    /// Dependent jobs that should run after this job completes successfully
    /// </summary>
    public Guid? DependsOnJobId { get; set; }

    #region Multi-Cycle Properties

    /// <summary>
    /// How cycles are managed for this job.
    /// </summary>
    public CycleMode CycleMode { get; set; } = CycleMode.SingleCycle;

    /// <summary>
    /// How sessions are handled between cycles.
    /// </summary>
    public CycleSessionMode CycleSessionMode { get; set; } = CycleSessionMode.ContinueSession;

    /// <summary>
    /// Maximum number of cycles to execute (default 1, meaning single execution).
    /// </summary>
    public int MaxCycles { get; set; } = 1;

    /// <summary>
    /// Current cycle number (1-based, updated during execution).
    /// </summary>
    public int CurrentCycle { get; set; } = 1;

    /// <summary>
    /// Custom prompt for cycle review in Autonomous mode.
    /// If empty, uses default: "Review all changes. Verify compile/tests. Respond with CYCLE_COMPLETE if done, otherwise continue."
    /// </summary>
    [StringLength(2000)]
    public string? CycleReviewPrompt { get; set; }

    #endregion
    /// <summary>
    /// Git diff showing changes made during job execution.
    /// Captured after job completes by comparing working directory state.
    /// </summary>
    public string? GitDiff { get; set; }

    /// <summary>
    /// Git commit hash at the start of job execution (for baseline comparison)
    /// </summary>
    public string? GitCommitBefore { get; set; }

    /// <summary>
    /// AI-generated summary of the work accomplished during this job.
    /// Used to pre-populate commit messages for one-click push.
    /// </summary>
    public string? SessionSummary { get; set; }

    /// <summary>
    /// Git commit hash after the user commits the job's results.
    /// When set, indicates the results have been committed to git.
    /// </summary>
    public string? GitCommitHash { get; set; }


    /// <summary>
    /// State machine for preserved local git changes captured before destructive branch operations.
    /// </summary>
    public GitCheckpointStatus GitCheckpointStatus { get; set; } = GitCheckpointStatus.None;

    /// <summary>
    /// Recovery branch that stores preserved local changes for this job, if one was created.
    /// </summary>
    [StringLength(250)]
    public string? GitCheckpointBranch { get; set; }

    /// <summary>
    /// Original branch that was active before the recovery checkpoint was created.
    /// </summary>
    [StringLength(250)]
    public string? GitCheckpointBaseBranch { get; set; }

    /// <summary>
    /// Commit hash of the preserved recovery checkpoint.
    /// </summary>
    [StringLength(100)]
    public string? GitCheckpointCommitHash { get; set; }

    /// <summary>
    /// Why the recovery checkpoint was created.
    /// </summary>
    [StringLength(500)]
    public string? GitCheckpointReason { get; set; }

    /// <summary>
    /// When the recovery checkpoint was created.
    /// </summary>
    public DateTime? GitCheckpointCapturedAt { get; set; }
	/// <summary>
	/// GitHub pull request number created for this job, if any.
	/// </summary>
	public int? PullRequestNumber { get; set; }

	/// <summary>
	/// GitHub pull request URL created for this job, if any.
	/// </summary>
	public string? PullRequestUrl { get; set; }

	/// <summary>
	/// When a pull request was created for this job.
	/// </summary>
	public DateTime? PullRequestCreatedAt { get; set; }

	/// <summary>
	/// When this job's branch changes were merged into the target branch.
	/// </summary>
	public DateTime? MergedAt { get; set; }

    /// <summary>
    /// Number of files changed during job execution.
    /// null = unchecked, 0 = verified no changes, >0 = changes detected.
    /// </summary>
    public int? ChangedFilesCount { get; set; }

    /// <summary>
    /// Whether the project's build verification passed after job completion.
    /// null = not checked (verification not enabled), true = passed, false = failed.
    /// When false, auto-commit and push are skipped to prevent broken code from being committed.
    /// </summary>
    public bool? BuildVerified { get; set; }

    /// <summary>
    /// Output from the build/test verification commands, captured for debugging.
    /// Only populated when BuildVerificationEnabled is true on the project.
    /// </summary>
    public string? BuildOutput { get; set; }

    /// <summary>
    /// Accumulated console output from the CLI process during execution.
    /// This is the full output log, separate from Output which contains the result summary.
    /// </summary>
    public string? ConsoleOutput { get; set; }

    /// <summary>
    /// The prompt/question from the CLI agent that requires user input.
    /// Set when Status is Paused.
    /// </summary>
    public string? PendingInteractionPrompt { get; set; }

    /// <summary>
    /// The type of interaction being requested (e.g., "confirmation", "input", "choice")
    /// </summary>
    [StringLength(50)]
    public string? InteractionType { get; set; }

    /// <summary>
    /// Available choices for the interaction (JSON array of options), if applicable
    /// </summary>
    public string? InteractionChoices { get; set; }

    /// <summary>
    /// Timestamp when the interaction was requested
    /// </summary>
    public DateTime? InteractionRequestedAt { get; set; }

    /// <summary>
    /// The auto-pilot iteration loop that created this job, if any.
    /// Null for manually created jobs.
    /// </summary>
    public Guid? IterationLoopId { get; set; }

    /// <summary>
    /// Groups all jobs in the same team swarm batch. All swarm members share the same SwarmId.
    /// Null for regular (non-swarm) jobs.
    /// </summary>
    public Guid? SwarmId { get; set; }

    /// <summary>
    /// The team role this job is executing as. Set when the job is part of a team swarm.
    /// Null for regular jobs or when no specific role is assigned.
    /// </summary>
    public Guid? AgentId { get; set; }

	public Agent? Agent { get; set; }

	[JsonIgnore]
	public JobStatistics? Statistics { get; set; }

	[JsonIgnore]
	public JobPlanningStatistics? PlanningStatistics { get; set; }

	[JsonIgnore]
	public JobExecutionStatistics? ExecutionStatistics { get; set; }

    /// <summary>
    /// Earliest time this job may be dequeued. Set when a rate-limited provider has no
    /// alternative and the job must back off until the rate limit resets.
    /// </summary>
    public DateTime? NotBeforeUtc { get; set; }

    /// <summary>
    /// Whether Playwright MCP was provided to this job for browser interaction.
    /// Set at execution start based on whether enabled web environments exist.
    /// </summary>
    public bool PlaywrightEnabled { get; set; }

    /// <summary>
    /// JSON snapshot of the environments exposed to this job at execution time.
    /// Stores names, URLs, types, and stages (no credentials) for audit and display.
    /// </summary>
    public string? EnvironmentsJson { get; set; }

    /// <summary>
    /// Number of environments exposed to this job at execution time.
    /// Stored explicitly so list queries can display the count without deserializing JSON.
    /// </summary>
    public int EnvironmentCount { get; set; }

    /// <summary>
    /// Gets the deserialized environment snapshots from EnvironmentsJson.
    /// Returns an empty list if no environments were captured.
    /// </summary>
    [NotMapped]
    public List<JobEnvironmentSnapshot> EnvironmentSnapshots
    {
        get
        {
            if (string.IsNullOrWhiteSpace(EnvironmentsJson))
            {
                return new List<JobEnvironmentSnapshot>();
            }

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<JobEnvironmentSnapshot>>(EnvironmentsJson) ?? new List<JobEnvironmentSnapshot>();
            }
            catch
            {
                return new List<JobEnvironmentSnapshot>();
            }
        }
    }

    public ICollection<JobMessage> Messages { get; set; } = new List<JobMessage>();

    [NotMapped]
    public int TotalMessageCount { get; set; }

    [NotMapped]
    public bool HasHiddenMessages => TotalMessageCount > Messages.Count;

    public ICollection<JobProviderAttempt> ProviderAttempts { get; set; } = new List<JobProviderAttempt>();

    /// <summary>
    /// Creates completion criteria from this job's settings.
    /// Uses job-level settings if available, falling back to provider settings, then defaults.
    /// </summary>
    public Services.JobCompletionCriteria GetCompletionCriteria()
    {
        // Priority: Job-level setting > Provider-level setting > Default (1 hour)
        TimeSpan maxExecutionTime;
        if (MaxExecutionMinutes.HasValue)
        {
            maxExecutionTime = TimeSpan.FromMinutes(MaxExecutionMinutes.Value);
        }
        else if (Provider?.MaxExecutionMinutes.HasValue == true)
        {
            maxExecutionTime = TimeSpan.FromMinutes(Provider.MaxExecutionMinutes.Value);
        }
        else
        {
            maxExecutionTime = TimeSpan.FromHours(1);
        }

        return new Services.JobCompletionCriteria
        {
            MaxExecutionTime = maxExecutionTime,
            MaxCostUsd = MaxCostUsd,
            MaxTokens = MaxTokens,
			StallTimeout = StallTimeoutSeconds.HasValue
				? TimeSpan.FromSeconds(StallTimeoutSeconds.Value)
				: Provider?.StallTimeoutSeconds is > 0
					? TimeSpan.FromSeconds(Provider.StallTimeoutSeconds.Value)
					: TimeSpan.FromMinutes(10),
            SuccessPattern = SuccessPattern,
            FailurePattern = FailurePattern
        };
    }

	private JobStatistics EnsureStatistics()
	{
		Statistics ??= new JobStatistics();
		Statistics.Job = this;
		Statistics.JobId = Id;
		return Statistics;
	}

	private JobPlanningStatistics EnsurePlanningStatistics()
	{
		PlanningStatistics ??= new JobPlanningStatistics();
		PlanningStatistics.Job = this;
		PlanningStatistics.JobId = Id;
		return PlanningStatistics;
	}

	private JobExecutionStatistics EnsureExecutionStatistics()
	{
		ExecutionStatistics ??= new JobExecutionStatistics();
		ExecutionStatistics.Job = this;
		ExecutionStatistics.JobId = Id;
		return ExecutionStatistics;
	}

	private void SyncStatisticsKeys()
	{
		if (Statistics is not null)
		{
			Statistics.JobId = Id;
			Statistics.Job = this;
		}

		if (PlanningStatistics is not null)
		{
			PlanningStatistics.JobId = Id;
			PlanningStatistics.Job = this;
		}

		if (ExecutionStatistics is not null)
		{
			ExecutionStatistics.JobId = Id;
			ExecutionStatistics.Job = this;
		}
	}
}

public enum JobStatus
{
    New,
    Pending,
    Started,
    Processing,
    Completed,
    Failed,
    Cancelled,
    /// <summary>
    /// Job stopped responding but may be recoverable
    /// </summary>
    Stalled,
    /// <summary>
    /// Job is paused waiting for user input/interaction
    /// </summary>
    Paused,
    /// <summary>
    /// Job is generating an implementation plan before execution.
    /// </summary>
    Planning
}
