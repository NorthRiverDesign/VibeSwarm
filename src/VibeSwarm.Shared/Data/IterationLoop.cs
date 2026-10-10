using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VibeSwarm.Shared.Data;

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
	/// Stopped because the provider's usage limit was reached. Only older loops end here:
	/// a running loop now waits for the limit to reset instead.
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

	public int CooldownSeconds { get; set; } = 60;

	/// <summary>
	/// After this many successful changes, the next iteration is a polish pass that reviews and
	/// tidies them instead of starting something new. 0 = never.
	/// </summary>
	public int PolishEveryIterations { get; set; } = 5;

	/// <summary>
	/// Whether each change is pushed after it is committed. Auto-pilot always commits its
	/// changes, so every iteration builds on the one before.
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

	public decimal TotalCostUsd { get; set; }
	public Guid? CurrentJobId { get; set; }
	public Job? CurrentJob { get; set; }
	public Guid? CurrentIdeaId { get; set; }

	/// <summary>
	/// Successful changes since the last polish pass.
	/// </summary>
	public int IterationsSinceLastPolish { get; set; }

	/// <summary>
	/// Idea rounds in a row that produced nothing to build, because every suggestion repeated
	/// earlier work or the idea source did not answer. Each miss moves to the next focus area.
	/// </summary>
	public int ConsecutiveIdeaMisses { get; set; }

	/// <summary>
	/// What the loop is waiting for right now, such as a usage limit reset. Null while it is
	/// simply working.
	/// </summary>
	[StringLength(500)]
	public string? StatusMessage { get; set; }

	/// <summary>Filled in for status responses; not stored.</summary>
	[NotMapped]
	public string? CurrentJobTitle { get; set; }

	[NotMapped]
	public JobStatus? CurrentJobStatus { get; set; }

	/// <summary>Set while the current job is held in the queue, e.g. until a usage limit resets.</summary>
	[NotMapped]
	public DateTime? CurrentJobNotBeforeUtc { get; set; }

	#endregion

	#region Timestamps

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime? StartedAt { get; set; }

	/// <summary>
	/// When the most recent iteration completed (success or failure).
	/// </summary>
	public DateTime? LastIterationAt { get; set; }

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
