using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Providers.OpenCode;

/// <summary>
/// Response from OpenCode REST API endpoints.
/// </summary>
public class OpenCodeApiResponse
{
	[JsonPropertyName("output")]
	public string? Output { get; set; }

	[JsonPropertyName("success")]
	public bool Success { get; set; }

	[JsonPropertyName("error")]
	public string? Error { get; set; }

	[JsonPropertyName("session_id")]
	public string? SessionId { get; set; }

	[JsonPropertyName("input_tokens")]
	public int? InputTokens { get; set; }

	[JsonPropertyName("output_tokens")]
	public int? OutputTokens { get; set; }

	[JsonPropertyName("cost_usd")]
	public decimal? CostUsd { get; set; }
}
