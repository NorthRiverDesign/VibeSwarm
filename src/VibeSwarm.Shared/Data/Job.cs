using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

public class Job
{
    public Guid Id { get; set; }

    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string GoalPrompt { get; set; } = string.Empty;

    public JobStatus Status { get; set; } = JobStatus.New;

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public Guid ProviderId { get; set; }
    public Provider? Provider { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

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
    public decimal? TotalCostUsd { get; set; }

    /// <summary>
    /// Total input tokens used
    /// </summary>
    public int? InputTokens { get; set; }

    /// <summary>
    /// Total output tokens used
    /// </summary>
    public int? OutputTokens { get; set; }

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
    /// Accumulated console output from the CLI process during execution.
    /// This is the full output log, separate from Output which contains the result summary.
    /// </summary>
    public string? ConsoleOutput { get; set; }

    public ICollection<JobMessage> Messages { get; set; } = new List<JobMessage>();

    /// <summary>
    /// Creates completion criteria from this job's settings
    /// </summary>
    public Services.JobCompletionCriteria GetCompletionCriteria()
    {
        return new Services.JobCompletionCriteria
        {
            MaxExecutionTime = MaxExecutionMinutes.HasValue
                ? TimeSpan.FromMinutes(MaxExecutionMinutes.Value)
                : TimeSpan.FromHours(1),
            MaxCostUsd = MaxCostUsd,
            MaxTokens = MaxTokens,
            StallTimeout = StallTimeoutSeconds.HasValue
                ? TimeSpan.FromSeconds(StallTimeoutSeconds.Value)
                : TimeSpan.FromMinutes(5),
            SuccessPattern = SuccessPattern,
            FailurePattern = FailurePattern
        };
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
    Stalled
}
