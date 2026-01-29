using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Providers.OpenCode;

/// <summary>
/// Represents a streaming event from the OpenCode CLI.
/// </summary>
public class OpenCodeStreamEvent
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("session_id")]
	public string? SessionId { get; set; }

	[JsonPropertyName("content")]
	public string? Content { get; set; }

	[JsonPropertyName("tool_name")]
	public string? ToolName { get; set; }

	[JsonPropertyName("tool_input")]
	public string? ToolInput { get; set; }

	[JsonPropertyName("tool_output")]
	public string? ToolOutput { get; set; }

	[JsonPropertyName("cost_usd")]
	public decimal? CostUsd { get; set; }

	[JsonPropertyName("input_tokens")]
	public int? InputTokens { get; set; }

	[JsonPropertyName("output_tokens")]
	public int? OutputTokens { get; set; }

	[JsonPropertyName("error")]
	public string? Error { get; set; }

	[JsonPropertyName("message")]
	public string? Message { get; set; }

	[JsonPropertyName("model")]
	public string? Model { get; set; }
}
