using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

/// <summary>
/// Lightweight job projection for list views. Excludes heavy text fields
/// (ConsoleOutput, Output, GitDiff, PlanningOutput, ExecutionPlan, BuildOutput, etc.)
/// that can be tens of MB per job.
/// </summary>
public class JobSummary
{
	public Guid Id { get; set; }
	public string? Title { get; set; }
	public string GoalPrompt { get; set; } = string.Empty;
	public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? GoalPrompt : Title;
	public JobStatus Status { get; set; }
	public Guid ProjectId { get; set; }
	public string? ProjectName { get; set; }
	public Guid ProviderId { get; set; }
	public string? ProviderName { get; set; }
	public string? ModelUsed { get; set; }
	public Guid? PlanningProviderId { get; set; }
	public string? PlanningProviderName { get; set; }
	public string? PlanningModelUsed { get; set; }
	public string? CurrentActivity { get; set; }
	public string? ErrorMessage { get; set; }
	public DateTime CreatedAt { get; set; }
	public DateTime? StartedAt { get; set; }
	public DateTime? CompletedAt { get; set; }
	public double? ExecutionDurationSeconds { get; set; }
	public decimal? TotalCostUsd { get; set; }
	public int? InputTokens { get; set; }
	public int? OutputTokens { get; set; }
	/// <summary>
	/// True when InputTokens/OutputTokens are estimates derived from text length
	/// rather than exact counts reported by the provider.
	/// </summary>
	public bool IsTokenEstimate { get; set; }
	public decimal? PlanningCostUsd { get; set; }
	public int? PlanningInputTokens { get; set; }
	public int? PlanningOutputTokens { get; set; }
	public decimal? ExecutionCostUsd { get; set; }
	public int? ExecutionInputTokens { get; set; }
	public int? ExecutionOutputTokens { get; set; }
	public int CurrentCycle { get; set; }
	public int MaxCycles { get; set; }
	public CycleMode CycleMode { get; set; }
	public string? AgentName { get; set; }
	public string? Branch { get; set; }
	public int? ChangedFilesCount { get; set; }
	public bool? BuildVerified { get; set; }
	public bool BuildVerificationEnabled { get; set; }
	public string? GitCommitHash { get; set; }
	public int? PullRequestNumber { get; set; }
	public string? PullRequestUrl { get; set; }
	public DateTime? MergedAt { get; set; }
	public bool IsPushed { get; set; }
	public string? SessionSummary { get; set; }
	public bool IsScheduled { get; set; }
	public Guid? JobScheduleId { get; set; }
	public bool PlaywrightEnabled { get; set; }
	public int EnvironmentCount { get; set; }

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
}
