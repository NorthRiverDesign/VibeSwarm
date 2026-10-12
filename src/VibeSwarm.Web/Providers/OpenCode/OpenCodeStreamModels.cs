using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSwarm.Shared.Providers.OpenCode;

/// <summary>
/// One line of <c>opencode run --format json</c> output. OpenCode 1.x and 2.x write the same shape:
/// <c>{"type":"text","timestamp":…,"sessionID":"ses_…","part":{…}}</c>, where type is text,
/// reasoning, tool_use, step_start, step_finish or error. Error events carry <c>error</c>
/// instead of <c>part</c>.
/// </summary>
public class OpenCodeStreamEvent
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("sessionID")]
	public string? SessionId { get; set; }

	/// <summary>
	/// The message part: text and reasoning parts have <c>text</c>; tool parts have <c>tool</c> and
	/// <c>state</c> (status, input, output or error); step-finish parts have <c>cost</c> and <c>tokens</c>.
	/// </summary>
	[JsonPropertyName("part")]
	public JsonElement Part { get; set; }

	/// <summary>
	/// 1.x sends <c>{"name":…,"data":{"message":…}}</c>; 2.x sends <c>{"message":…}</c>.
	/// </summary>
	[JsonPropertyName("error")]
	public JsonElement Error { get; set; }
}
