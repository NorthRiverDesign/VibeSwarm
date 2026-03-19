using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Status of an auto-pilot iteration loop.
/// </summary>
public enum IterationLoopStatus
{
	/// <summary>
	/// Created but not yet started, or reset after completion.
	/// </summary>
	Idle = 0,

	/// <summary>
	/// Actively iterating: generating ideas, executing jobs, evaluating results.
	/// </summary>
	Running = 1,

	/// <summary>
	/// User paused the loop. Can be resumed.
	/// </summary>
	Paused = 2,

	/// <summary>
	/// Stop requested. Waiting for the current job to finish before transitioning to Stopped.
	/// </summary>
	Stopping = 3,

	/// <summary>
	/// Gracefully stopped by user or after completing all iterations.
	/// </summary>
	Stopped = 4,

	/// <summary>
	/// Stopped because the provider's usage limit was reached.
	/// </summary>
	Exhausted = 5,

	/// <summary>
	/// Stopped because too many consecutive job failures occurred.
	/// </summary>
	Failed = 6
}

/// <summary>
/// Represents an auto-pilot iteration loop for a project.
/// When running, the loop autonomously scans the codebase, generates improvement ideas,
/// executes them as jobs, evaluates results, and repeats until a guardrail triggers.
/// </summary>
public class IterationLoop
{
	public Guid Id { get; set; }

	/// <summary>
	/// The project this loop belongs to. Only one loop may be active per project at a time.
	/// </summary>
	public Guid ProjectId { get; set; }
	public Project? Project { get; set; }

	public IterationLoopStatus Status { get; set; } = IterationLoopStatus.Idle;

	#region Configuration

	/// <summary>
	/// Inference provider (e.g., Grok, Ollama) used for idea generation.
	/// If null, falls back to the coding provider or project default.
	/// </summary>
	public Guid? InferenceProviderId { get; set; }

	/// <summary>
	/// Model to use for inference-based idea generation.
	/// </summary>
	[StringLength(200)]
	public string? InferenceModelId { get; set; }

	/// <summary>
	/// CLI coding provider (e.g., Claude, Copilot) used for job execution.
	/// If null, uses the project's default provider selection.
	/// </summary>
	public Guid? ProviderId { get; set; }

	/// <summary>
	/// Optional model override for the coding provider.
	/// </summary>
	[StringLength(200)]
	public string? ModelId { get; set; }

	/// <summary>
	/// Maximum number of iterations before the loop stops. 0 = unlimited.
	/// </summary>
	public int MaxIterations { get; set; } = 50;

	/// <summary>
	/// Maximum total cost in USD across all iterations. Null = no cost limit.
	/// </summary>
	public decimal? MaxTotalCostUsd { get; set; }

	/// <summary>
	/// Number of consecutive job failures before the loop stops.
	/// </summary>
	public int MaxConsecutiveFailures { get; set; } = 3;

	/// <summary>
	/// Seconds to wait between iterations (cooldown).
	/// </summary>
	public int CooldownSeconds { get; set; } = 60;

	/// <summary>
	/// Whether to auto-commit changes after each successful job.
	/// </summary>
	public bool AutoCommit { get; set; } = true;

	/// <summary>
	/// Whether to auto-push after committing.
	/// </summary>
	public bool AutoPush { get; set; }

	#endregion

	#region Runtime State

	/// <summary>
	/// Number of iterations completed (successful or failed).
	/// </summary>
	public int CompletedIterations { get; set; }

	/// <summary>
	/// Number of consecutive failures. Reset to 0 on success.
	/// </summary>
	public int ConsecutiveFailures { get; set; }

	/// <summary>
	/// Accumulated cost across all iterations in this loop run.
	/// </summary>
	public decimal TotalCostUsd { get; set; }

	/// <summary>
	/// The job currently being executed by this iteration, if any.
	/// </summary>
	public Guid? CurrentJobId { get; set; }
	public Job? CurrentJob { get; set; }

	/// <summary>
	/// The idea generated for the current iteration, if any.
	/// </summary>
	public Guid? CurrentIdeaId { get; set; }

	#endregion

	#region Timestamps

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	public DateTime? StartedAt { get; set; }

	/// <summary>
	/// When the most recent iteration completed (success or failure).
	/// </summary>
	public DateTime? LastIterationAt { get; set; }

	/// <summary>
	/// When the loop was stopped or finished.
	/// </summary>
	public DateTime? StoppedAt { get; set; }

	/// <summary>
	/// Earliest time the next iteration may start (cooldown target).
	/// </summary>
	public DateTime? NextIterationAt { get; set; }

	#endregion

	#region Diagnostics

	/// <summary>
	/// Human-readable reason the loop stopped (e.g., "Max iterations reached", "User stopped").
	/// </summary>
	[StringLength(500)]
	public string? LastStopReason { get; set; }

	/// <summary>
	/// JSON snapshot of the most recent CLI usage check result.
	/// </summary>
	public string? LastUsageCheckResult { get; set; }

	#endregion
}
