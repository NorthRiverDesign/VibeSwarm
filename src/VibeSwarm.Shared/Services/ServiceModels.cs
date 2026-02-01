using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Criteria for determining when a job is complete
/// </summary>
public class JobCompletionCriteria
{
	/// <summary>
	/// Maximum time a job can run before being considered timed out
	/// </summary>
	public TimeSpan? MaxExecutionTime { get; set; }

	/// <summary>
	/// Maximum tokens that can be used (input + output)
	/// </summary>
	public int? MaxTokens { get; set; }

	/// <summary>
	/// Maximum cost in USD before stopping
	/// </summary>
	public decimal? MaxCostUsd { get; set; }

	/// <summary>
	/// Time without activity before job is considered stalled
	/// </summary>
	public TimeSpan? StallTimeout { get; set; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Regex pattern that indicates success when found in output
	/// </summary>
	public string? SuccessPattern { get; set; }

	/// <summary>
	/// Regex pattern that indicates failure when found in output or error
	/// </summary>
	public string? FailurePattern { get; set; }

	/// <summary>
	/// Creates default completion criteria
	/// </summary>
	public static JobCompletionCriteria Default => new()
	{
		MaxExecutionTime = TimeSpan.FromHours(1),
		StallTimeout = TimeSpan.FromMinutes(5)
	};

	/// <summary>
	/// Creates completion criteria for long-running jobs
	/// </summary>
	public static JobCompletionCriteria LongRunning => new()
	{
		MaxExecutionTime = TimeSpan.FromHours(8),
		StallTimeout = TimeSpan.FromMinutes(15)
	};
}

/// <summary>
/// Summary of work accomplished during a provider session
/// </summary>
public class SessionSummary
{
	/// <summary>
	/// Whether the summary was successfully retrieved
	/// </summary>
	public bool Success { get; set; }

	/// <summary>
	/// A concise summary suitable for a commit message (typically 1-3 lines)
	/// </summary>
	public string? Summary { get; set; }

	/// <summary>
	/// A more detailed description of changes made
	/// </summary>
	public string? DetailedDescription { get; set; }

	/// <summary>
	/// List of files that were modified (if available)
	/// </summary>
	public List<string> ModifiedFiles { get; set; } = new();

	/// <summary>
	/// Error message if summary retrieval failed
	/// </summary>
	public string? ErrorMessage { get; set; }

	/// <summary>
	/// The source of the summary (e.g., "session", "output", "fallback")
	/// </summary>
	public string? Source { get; set; }
}

/// <summary>
/// Result of a state transition attempt
/// </summary>
public class StateTransitionResult
{
	public bool Success { get; set; }
	public JobStatus PreviousStatus { get; set; }
	public JobStatus NewStatus { get; set; }
	public string? ErrorMessage { get; set; }
	public string? TransitionReason { get; set; }
	public DateTime TransitionTime { get; set; }
}

/// <summary>
/// Result of evaluating completion criteria
/// </summary>
public class CompletionEvaluation
{
	public Guid JobId { get; set; }
	public DateTime EvaluatedAt { get; set; }
	public bool IsComplete { get; set; }
	public bool ShouldFail { get; set; }
	public bool ShouldRetry { get; set; }
	public string? CompletionReason { get; set; }
	public JobCompletionCriteria? Criteria { get; set; }
}
