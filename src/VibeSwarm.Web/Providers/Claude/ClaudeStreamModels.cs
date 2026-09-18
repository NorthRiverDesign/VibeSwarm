using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Providers.Claude;

/// <summary>
/// Represents a streaming event from the Claude CLI in stream-json output format.
/// </summary>
public class ClaudeStreamEvent
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("subtype")]
	public string? Subtype { get; set; }

	[JsonPropertyName("session_id")]
	public string? SessionId { get; set; }

	[JsonPropertyName("message")]
	public ClaudeMessage? Message { get; set; }

	// Result event fields
	[JsonPropertyName("result")]
	public string? Result { get; set; }

	[JsonPropertyName("total_cost_usd")]
	public decimal? TotalCostUsd { get; set; }

	[JsonPropertyName("usage")]
	public ClaudeUsageInfo? Usage { get; set; }

	// Alternative field names that Claude CLI might use
	[JsonPropertyName("cost_usd")]
	public decimal? CostUsd { get; set; }

	// Flat token fields - some Claude CLI versions output these directly on the result event
	[JsonPropertyName("input_tokens")]
	public int? InputTokens { get; set; }

	[JsonPropertyName("output_tokens")]
	public int? OutputTokens { get; set; }

	[JsonPropertyName("num_turns")]
	public int? NumTurns { get; set; }

	[JsonPropertyName("duration_ms")]
	public double? DurationMs { get; set; }

	[JsonPropertyName("duration_api_ms")]
	public double? DurationApiMs { get; set; }

	// Error fields - present when the CLI encounters system-level errors
	// (e.g., model unavailable, upstream outages, authentication failures)
	[JsonPropertyName("error")]
	public string? Error { get; set; }

	[JsonPropertyName("is_error")]
	public bool? IsError { get; set; }

	[JsonPropertyName("stop_reason")]
	public string? StopReason { get; set; }

	// Present on "rate_limit_event" messages, which the CLI emits in stream-json mode
	// whenever the account's usage windows change.
	[JsonPropertyName("rate_limit_info")]
	public ClaudeRateLimitInfo? RateLimitInfo { get; set; }
}

/// <summary>
/// Represents a message in Claude's streaming output.
/// </summary>
public class ClaudeMessage
{
	[JsonPropertyName("id")]
	public string? Id { get; set; }

	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("role")]
	public string? Role { get; set; }

	[JsonPropertyName("model")]
	public string? Model { get; set; }

	[JsonPropertyName("content")]
	public ClaudeContentBlock[]? Content { get; set; }

	[JsonPropertyName("usage")]
	public ClaudeUsageInfo? Usage { get; set; }

	[JsonPropertyName("stop_reason")]
	public string? StopReason { get; set; }
}

/// <summary>
/// Represents a content block in Claude's message (text, tool_use, tool_result, thinking).
/// </summary>
public class ClaudeContentBlock
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	// For text content
	[JsonPropertyName("text")]
	public string? Text { get; set; }

	// For thinking content (interleaved thinking, v1.0.0+)
	[JsonPropertyName("thinking")]
	public string? Thinking { get; set; }

	// For tool_use content
	[JsonPropertyName("id")]
	public string? Id { get; set; }

	[JsonPropertyName("name")]
	public string? Name { get; set; }

	[JsonPropertyName("input")]
	public System.Text.Json.JsonElement? Input { get; set; }

	// For tool_result content
	[JsonPropertyName("tool_use_id")]
	public string? ToolUseId { get; set; }

	[JsonPropertyName("content")]
	public string? Content { get; set; }

	[JsonPropertyName("is_error")]
	public bool? IsError { get; set; }
}

/// <summary>
/// Token usage information from Claude.
/// </summary>
public class ClaudeUsageInfo
{
	[JsonPropertyName("input_tokens")]
	public int? InputTokens { get; set; }

	[JsonPropertyName("output_tokens")]
	public int? OutputTokens { get; set; }

	[JsonPropertyName("cache_read_input_tokens")]
	public int? CacheReadInputTokens { get; set; }

	[JsonPropertyName("cache_creation_input_tokens")]
	public int? CacheCreationInputTokens { get; set; }
}

/// <summary>
/// Usage limit state reported by a "rate_limit_event" stream message.
/// </summary>
/// <remarks>
/// Verified against Claude Code 2.1.276. Unlike the human-readable warnings on stderr,
/// this payload is structured and arrives during a normal headless run, so it is the
/// preferred source of limit data.
/// </remarks>
public class ClaudeRateLimitInfo
{
	/// <summary>
	/// Overall state, e.g. "allowed" or "allowed_warning". Values that do not begin with
	/// "allowed" are treated as the limit having been hit.
	/// </summary>
	[JsonPropertyName("status")]
	public string? Status { get; set; }

	/// <summary>
	/// Which limit is currently binding, e.g. "overage".
	/// </summary>
	[JsonPropertyName("rateLimitType")]
	public string? RateLimitType { get; set; }

	/// <summary>
	/// Fraction of the binding limit consumed, from 0 to 1 (and above 1 once exceeded).
	/// </summary>
	[JsonPropertyName("utilization")]
	public double? Utilization { get; set; }

	/// <summary>
	/// Unix epoch seconds when the binding limit resets.
	/// </summary>
	[JsonPropertyName("resetsAt")]
	public long? ResetsAt { get; set; }

	/// <summary>
	/// Whether the account is currently drawing on paid overage.
	/// </summary>
	[JsonPropertyName("isUsingOverage")]
	public bool? IsUsingOverage { get; set; }

	/// <summary>
	/// Utilization fraction at which the CLI started warning.
	/// </summary>
	[JsonPropertyName("surpassedThreshold")]
	public double? SurpassedThreshold { get; set; }

	/// <summary>
	/// The subscription's rolling windows, keyed by the CLI's own window name.
	/// </summary>
	/// <remarks>
	/// Deliberately a dictionary rather than fixed properties. The set is not stable:
	/// a Haiku run reports only <c>five_hour</c> and <c>seven_day</c>, while a Fable run
	/// on the same account also reports <c>seven_day_overage_included</c>. Binding named
	/// properties silently drops whatever Anthropic adds next.
	/// </remarks>
	[JsonPropertyName("unifiedWindows")]
	public Dictionary<string, ClaudeRateLimitWindow>? UnifiedWindows { get; set; }
}

/// <summary>
/// A single rolling usage window.
/// </summary>
public class ClaudeRateLimitWindow
{
	/// <summary>
	/// Fraction of the window consumed, from 0 to 1.
	/// </summary>
	[JsonPropertyName("utilization")]
	public double? Utilization { get; set; }

	/// <summary>
	/// Unix epoch seconds when the window resets.
	/// </summary>
	[JsonPropertyName("resetsAt")]
	public long? ResetsAt { get; set; }
}
