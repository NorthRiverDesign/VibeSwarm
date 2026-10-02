using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Services;

public class JobCompletionCriteria
{
	public static readonly TimeSpan DefaultStallTimeoutValue = TimeSpan.FromMinutes(15);

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
	public TimeSpan? StallTimeout { get; set; } = DefaultStallTimeoutValue;

	/// <summary>
	/// Regex pattern that indicates success when found in output
	/// </summary>
	public string? SuccessPattern { get; set; }

	/// <summary>
	/// Regex pattern that indicates failure when found in output or error
	/// </summary>
	public string? FailurePattern { get; set; }

	public static JobCompletionCriteria Default => new()
	{
		MaxExecutionTime = TimeSpan.FromHours(1),
		StallTimeout = DefaultStallTimeoutValue
	};

	public static JobCompletionCriteria LongRunning => new()
	{
		MaxExecutionTime = TimeSpan.FromHours(8),
		StallTimeout = DefaultStallTimeoutValue
	};
}

public class SessionSummary
{
	public bool Success { get; set; }

	/// <summary>
	/// A concise summary suitable for a commit message (typically 1-3 lines)
	/// </summary>
	public string? Summary { get; set; }

	public string? DetailedDescription { get; set; }
	public List<string> ModifiedFiles { get; set; } = new();
	public string? ErrorMessage { get; set; }

	/// <summary>
	/// The source of the summary (e.g., "session", "output", "fallback")
	/// </summary>
	public string? Source { get; set; }
}

public class StateTransitionResult
{
	public bool Success { get; set; }
	public JobStatus PreviousStatus { get; set; }
	public JobStatus NewStatus { get; set; }
	public string? ErrorMessage { get; set; }
	public string? TransitionReason { get; set; }
	public DateTime TransitionTime { get; set; }
}

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
