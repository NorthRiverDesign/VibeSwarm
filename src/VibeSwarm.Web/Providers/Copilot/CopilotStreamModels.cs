using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Providers.Copilot;

/// <summary>
/// Represents a streaming event from the GitHub Copilot CLI.
/// </summary>
public class CopilotStreamEvent
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("content")]
	public string? Content { get; set; }

	[JsonPropertyName("suggestion")]
	public string? Suggestion { get; set; }

	[JsonPropertyName("error")]
	public string? Error { get; set; }

	[JsonPropertyName("message")]
	public string? Message { get; set; }

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
