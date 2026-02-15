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
}

/// <summary>
/// Represents a content block in Claude's message (text, tool_use, tool_result).
/// </summary>
public class ClaudeContentBlock
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	// For text content
	[JsonPropertyName("text")]
	public string? Text { get; set; }

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
