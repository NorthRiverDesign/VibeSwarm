using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Providers.Claude;

/// <summary>
/// Response from Claude REST API messages endpoint.
/// </summary>
public class ClaudeApiResponse
{
	[JsonPropertyName("id")]
	public string? Id { get; set; }

	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("role")]
	public string? Role { get; set; }

	[JsonPropertyName("content")]
	public ClaudeContentBlock[]? Content { get; set; }

	[JsonPropertyName("model")]
	public string? Model { get; set; }

	[JsonPropertyName("stop_reason")]
	public string? StopReason { get; set; }

	[JsonPropertyName("usage")]
	public ClaudeUsageInfo? Usage { get; set; }
}
