using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSwarm.Shared.Providers.Claude;

namespace VibeSwarm.Shared.Providers.Copilot;

/// <summary>
/// Represents a streaming event from the GitHub Copilot CLI.
/// Supports both native Copilot format and Claude Code format (used when
/// Copilot CLI operates with Claude models under the hood).
/// </summary>
public class CopilotStreamEvent
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("subtype")]
	public string? Subtype { get; set; }

	/// <summary>
	/// Content field - can be a string (native Copilot format) or an array of content blocks (Claude Code format).
	/// Use <see cref="ContentText"/> to get a string value or <see cref="ParseContentBlocks"/> to extract
	/// Claude-format content blocks when the content is an array.
	/// </summary>
	[JsonPropertyName("content")]
	public JsonElement? Content { get; set; }

	[JsonPropertyName("suggestion")]
	public string? Suggestion { get; set; }

	[JsonPropertyName("error")]
	public string? Error { get; set; }

	/// <summary>
	/// Message field - can be a string (native Copilot format) or a JSON object (Claude Code format).
	/// Use <see cref="MessageText"/> to get a string value or <see cref="ParseClaudeMessage"/> to extract
	/// a <see cref="ClaudeMessage"/> when the Copilot CLI outputs Claude-format JSON.
	/// </summary>
	[JsonPropertyName("message")]
	public JsonElement? Message { get; set; }

	/// <summary>
	/// Indicates whether the result event represents an error (Claude Code format).
	/// </summary>
	[JsonPropertyName("is_error")]
	public bool? IsError { get; set; }

	/// <summary>
	/// Result text from a result event (Claude Code format).
	/// </summary>
	[JsonPropertyName("result")]
	public string? Result { get; set; }

	// Session tracking (v0.0.372+)
	[JsonPropertyName("session_id")]
	public string? SessionId { get; set; }

	// Token usage fields
	[JsonPropertyName("input_tokens")]
	public int? InputTokens { get; set; }

	[JsonPropertyName("output_tokens")]
	public int? OutputTokens { get; set; }

	[JsonPropertyName("cost_usd")]
	public decimal? CostUsd { get; set; }

	[JsonPropertyName("total_cost_usd")]
	public decimal? TotalCostUsd { get; set; }

	[JsonPropertyName("usage")]
	public CopilotUsageInfo? Usage { get; set; }

	[JsonPropertyName("model")]
	public string? Model { get; set; }

	// Premium request tracking (Copilot-specific)
	[JsonPropertyName("premium_requests")]
	public int? PremiumRequests { get; set; }

	// Tool execution details
	[JsonPropertyName("tool_name")]
	public string? ToolName { get; set; }

	[JsonPropertyName("tool_input")]
	public string? ToolInput { get; set; }

	[JsonPropertyName("tool_output")]
	public string? ToolOutput { get; set; }

	// Reasoning summaries for GPT models (v0.0.403+)
	[JsonPropertyName("reasoning")]
	public string? Reasoning { get; set; }

	[JsonPropertyName("reasoning_summary")]
	public string? ReasoningSummary { get; set; }

	// Plan mode events (v0.0.412+)
	[JsonPropertyName("plan")]
	public string? Plan { get; set; }

	// Additional Claude Code format fields
	[JsonPropertyName("num_turns")]
	public int? NumTurns { get; set; }

	[JsonPropertyName("duration_ms")]
	public double? DurationMs { get; set; }

	[JsonPropertyName("duration_api_ms")]
	public double? DurationApiMs { get; set; }

	/// <summary>
	/// Extracts the content as a plain string when it is a JSON string value.
	/// Returns null if the content is an array/object or missing.
	/// </summary>
	public string? ContentText =>
		Content?.ValueKind == JsonValueKind.String ? Content.Value.GetString() : null;

	/// <summary>
	/// Attempts to parse the content field as Claude-format content blocks.
	/// This handles the case where the CLI outputs content as an array of objects
	/// at the root level (e.g., [{"type":"text","text":"..."}]).
	/// </summary>
	public ClaudeContentBlock[]? ParseContentBlocks()
	{
		if (Content?.ValueKind != JsonValueKind.Array)
			return null;

		try
		{
			return Content.Value.Deserialize<ClaudeContentBlock[]>();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Extracts the message as a plain string when it is a JSON string value.
	/// Returns null if the message is an object or missing.
	/// </summary>
	public string? MessageText =>
		Message?.ValueKind == JsonValueKind.String ? Message.Value.GetString() : null;

	/// <summary>
	/// Attempts to parse the message field as a <see cref="ClaudeMessage"/> object.
	/// This is needed when the Copilot CLI outputs Claude Code format where the
	/// message field is a complex object containing content, usage, and model info.
	/// </summary>
	public ClaudeMessage? ParseClaudeMessage()
	{
		if (Message?.ValueKind != JsonValueKind.Object)
			return null;

		try
		{
			return Message.Value.Deserialize<ClaudeMessage>();
		}
		catch
		{
			return null;
		}
	}
}

/// <summary>
/// Token usage information from Copilot.
/// </summary>
public class CopilotUsageInfo
{
	[JsonPropertyName("input_tokens")]
	public int? InputTokens { get; set; }

	[JsonPropertyName("output_tokens")]
	public int? OutputTokens { get; set; }

	[JsonPropertyName("total_tokens")]
	public int? TotalTokens { get; set; }

	[JsonPropertyName("cached_tokens")]
	public int? CachedTokens { get; set; }
}
