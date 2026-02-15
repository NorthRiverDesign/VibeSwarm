using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Manages job state transitions with validation and ensures jobs follow
/// a consistent lifecycle from creation to completion.
/// </summary>
public class JobStateMachine
{
	private static readonly Dictionary<JobStatus, HashSet<JobStatus>> ValidTransitions = new()
	{
		[JobStatus.New] = new HashSet<JobStatus>
		{
			JobStatus.Pending,
			JobStatus.Started,
			JobStatus.Cancelled
		},
		[JobStatus.Pending] = new HashSet<JobStatus>
		{
			JobStatus.Started,
			JobStatus.Cancelled,
			JobStatus.New // Allow reset
        },
		[JobStatus.Started] = new HashSet<JobStatus>
		{
			JobStatus.Processing,
			JobStatus.Completed,
			JobStatus.Failed,
			JobStatus.Cancelled,
			JobStatus.Stalled,
			JobStatus.Paused,
			JobStatus.New // Allow reset for retry
        },
		[JobStatus.Processing] = new HashSet<JobStatus>
		{
			JobStatus.Completed,
			JobStatus.Failed,
			JobStatus.Cancelled,
			JobStatus.Stalled,
			JobStatus.Paused,
			JobStatus.Started, // Allow transition back if interrupted
            JobStatus.New // Allow reset for retry
        },
		[JobStatus.Paused] = new HashSet<JobStatus>
		{
			JobStatus.Processing, // Resume after interaction
			JobStatus.Started,    // Re-start after pause
			JobStatus.Cancelled,
			JobStatus.Failed,
			JobStatus.Stalled,
			JobStatus.New // Allow reset for retry
		},
		[JobStatus.Stalled] = new HashSet<JobStatus>
		{
			JobStatus.New, // Reset for retry
            JobStatus.Failed,
			JobStatus.Cancelled
		},
		[JobStatus.Completed] = new HashSet<JobStatus>
		{
			JobStatus.New // Allow re-run
        },
		[JobStatus.Failed] = new HashSet<JobStatus>
		{
			JobStatus.New // Allow retry
        },
		[JobStatus.Cancelled] = new HashSet<JobStatus>
		{
			JobStatus.New // Allow restart
        }
	};

	/// <summary>
	/// Checks if a state transition is valid
	/// </summary>
	public static bool CanTransition(JobStatus from, JobStatus to)
	{
		if (from == to) return true; // Same state is always allowed
		return ValidTransitions.TryGetValue(from, out var validTargets) && validTargets.Contains(to);
	}

	/// <summary>
	/// Gets all valid target states from the current state
	/// </summary>
	public static IReadOnlySet<JobStatus> GetValidTransitions(JobStatus from)
	{
		return ValidTransitions.TryGetValue(from, out var targets)
			? targets
			: new HashSet<JobStatus>();
	}

	/// <summary>
	/// Checks if a job is in a terminal state (no more work will be done)
	/// </summary>
	public static bool IsTerminalState(JobStatus status)
	{
		return status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
	}

	/// <summary>
	/// Checks if a job is in an active state (work is being done or waiting for interaction)
	/// </summary>
	public static bool IsActiveState(JobStatus status)
	{
		return status is JobStatus.Started or JobStatus.Processing or JobStatus.Paused;
	}

	/// <summary>
	/// Checks if a job is in a waiting state (queued but not started)
	/// </summary>
	public static bool IsWaitingState(JobStatus status)
	{
		return status is JobStatus.New or JobStatus.Pending;
	}

	/// <summary>
	/// Checks if a job can be cancelled
	/// </summary>
	public static bool CanCancel(JobStatus status)
	{
		return !IsTerminalState(status);
	}

	/// <summary>
	/// Checks if a job can be retried
	/// </summary>
	public static bool CanRetry(JobStatus status)
	{
		return status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Stalled;
	}

	/// <summary>
	/// Attempts a state transition and returns the result
	/// </summary>
	public static StateTransitionResult TryTransition(Job job, JobStatus newStatus, string? reason = null)
	{
		var currentStatus = job.Status;

		if (!CanTransition(currentStatus, newStatus))
		{
			return new StateTransitionResult
			{
				Success = false,
				PreviousStatus = currentStatus,
				NewStatus = currentStatus,
				ErrorMessage = $"Invalid state transition from {currentStatus} to {newStatus}"
			};
		}

		// Apply the transition
		job.Status = newStatus;

		// Set timestamps based on transition
		var now = DateTime.UtcNow;
		switch (newStatus)
		{
			case JobStatus.Started:
				job.StartedAt ??= now;
				job.LastActivityAt = now;
				job.LastHeartbeatAt = now;
				break;

			case JobStatus.Processing:
				job.LastActivityAt = now;
				job.LastHeartbeatAt = now;
				break;

			case JobStatus.Completed:
			case JobStatus.Failed:
			case JobStatus.Cancelled:
				job.CompletedAt = now;
				job.CurrentActivity = null;
				job.WorkerInstanceId = null;
				job.ProcessId = null;
				break;

			case JobStatus.Stalled:
				job.LastActivityAt = now;
				break;

			case JobStatus.New:
				// Reset for retry
				job.StartedAt = null;
				job.CompletedAt = null;
				job.CurrentActivity = null;
				job.WorkerInstanceId = null;
				job.ProcessId = null;
				job.LastHeartbeatAt = null;
				job.CancellationRequested = false;
				break;
		}

		return new StateTransitionResult
		{
			Success = true,
			PreviousStatus = currentStatus,
			NewStatus = newStatus,
			TransitionReason = reason,
			TransitionTime = now
		};
	}

	/// <summary>
	/// Evaluates if a job has met its completion criteria
	/// </summary>
	public static CompletionEvaluation EvaluateCompletion(Job job, JobCompletionCriteria criteria)
	{
		var evaluation = new CompletionEvaluation
		{
			JobId = job.Id,
			EvaluatedAt = DateTime.UtcNow,
			Criteria = criteria
		};

		// Check if already in terminal state
		if (IsTerminalState(job.Status))
		{
			evaluation.IsComplete = true;
			evaluation.CompletionReason = $"Job reached terminal state: {job.Status}";
			return evaluation;
		}

		// Check timeout
		if (criteria.MaxExecutionTime.HasValue && job.StartedAt.HasValue)
		{
			var elapsed = DateTime.UtcNow - job.StartedAt.Value;
			if (elapsed > criteria.MaxExecutionTime.Value)
			{
				evaluation.IsComplete = true;
				evaluation.ShouldFail = true;
				evaluation.CompletionReason = $"Exceeded maximum execution time of {criteria.MaxExecutionTime.Value}";
				return evaluation;
			}
		}

		// Check token limits
		if (criteria.MaxTokens.HasValue && job.OutputTokens.HasValue)
		{
			if (job.OutputTokens.Value >= criteria.MaxTokens.Value)
			{
				evaluation.IsComplete = true;
				evaluation.CompletionReason = $"Reached token limit of {criteria.MaxTokens.Value}";
				return evaluation;
			}
		}

		// Check cost limit
		if (criteria.MaxCostUsd.HasValue && job.TotalCostUsd.HasValue)
		{
			if (job.TotalCostUsd.Value >= criteria.MaxCostUsd.Value)
			{
				evaluation.IsComplete = true;
				evaluation.CompletionReason = $"Reached cost limit of ${criteria.MaxCostUsd.Value}";
				return evaluation;
			}
		}

		// Check stall timeout
		if (criteria.StallTimeout.HasValue && job.LastActivityAt.HasValue)
		{
			var timeSinceActivity = DateTime.UtcNow - job.LastActivityAt.Value;
			if (timeSinceActivity > criteria.StallTimeout.Value)
			{
				evaluation.IsComplete = true;
				evaluation.ShouldRetry = job.RetryCount < job.MaxRetries;
				evaluation.CompletionReason = $"Job stalled for {timeSinceActivity}";
				return evaluation;
			}
		}

		// Check retry limit
		if (job.RetryCount >= job.MaxRetries && job.MaxRetries > 0)
		{
			if (job.Status == JobStatus.Stalled)
			{
				evaluation.IsComplete = true;
				evaluation.ShouldFail = true;
				evaluation.CompletionReason = $"Exceeded maximum retry count of {job.MaxRetries}";
				return evaluation;
			}
		}

		// Check custom success patterns in output
		if (!string.IsNullOrEmpty(criteria.SuccessPattern) && !string.IsNullOrEmpty(job.Output))
		{
			if (System.Text.RegularExpressions.Regex.IsMatch(job.Output, criteria.SuccessPattern))
			{
				evaluation.IsComplete = true;
				evaluation.CompletionReason = "Output matched success pattern";
				return evaluation;
			}
		}

		// Check custom failure patterns in output or error
		if (!string.IsNullOrEmpty(criteria.FailurePattern))
		{
			var textToCheck = $"{job.Output}\n{job.ErrorMessage}";
			if (System.Text.RegularExpressions.Regex.IsMatch(textToCheck, criteria.FailurePattern))
			{
				evaluation.IsComplete = true;
				evaluation.ShouldFail = true;
				evaluation.CompletionReason = "Output matched failure pattern";
				return evaluation;
			}
		}

		evaluation.IsComplete = false;
		return evaluation;
	}
}
