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

    public ICollection<JobMessage> Messages { get; set; } = new List<JobMessage>();
}

public enum JobStatus
{
    New,
    Pending,
    Started,
    Processing,
    Completed,
    Failed,
    Cancelled
}
