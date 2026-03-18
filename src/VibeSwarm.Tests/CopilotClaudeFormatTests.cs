using System.Text.Json;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Providers.Copilot;

namespace VibeSwarm.Tests;

/// <summary>
/// Tests that CopilotStreamEvent correctly deserializes and handles Claude Code-format JSON output
/// (used when the Copilot CLI operates with Claude models under the hood).
/// </summary>
public sealed class CopilotClaudeFormatTests
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
	};

	#region CopilotStreamEvent deserialization with Claude-format JSON

	[Fact]
	public void Deserializes_SystemInitEvent()
	{
		var json = """{"type":"system","subtype":"init","session_id":"abc-123","tools":[]}""";
		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("system", evt!.Type);
		Assert.Equal("init", evt.Subtype);
		Assert.Equal("abc-123", evt.SessionId);
	}

	[Fact]
	public void Deserializes_AssistantEvent_WithObjectMessage()
	{
		var json = """
		{
			"type": "assistant",
			"message": {
				"id": "msg_01",
				"type": "message",
				"role": "assistant",
				"content": [{"type": "text", "text": "Hello world"}],
				"model": "claude-sonnet-4-20250514",
				"stop_reason": "end_turn",
				"usage": {"input_tokens": 1000, "output_tokens": 50}
			},
			"session_id": "sess-456"
		}
		""";

		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("assistant", evt!.Type);
		Assert.Equal("sess-456", evt.SessionId);
		Assert.NotNull(evt.Message);
		Assert.Equal(JsonValueKind.Object, evt.Message!.Value.ValueKind);
	}

	[Fact]
	public void Deserializes_AssistantEvent_WithStringMessage()
	{
		var json = """{"type":"assistant","message":"simple text message"}""";
		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("simple text message", evt!.MessageText);
	}

	[Fact]
	public void Deserializes_AssistantEvent_WithError()
	{
		var json = """
		{
			"type": "assistant",
			"message": {
				"id": "msg_01",
				"type": "message",
				"role": "assistant",
				"content": [],
				"model": "<synthetic>",
				"stop_reason": "end_turn"
			},
			"error": "invalid_request",
			"session_id": "sess-789"
		}
		""";

		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("assistant", evt!.Type);
		Assert.Equal("invalid_request", evt.Error);
		Assert.Equal("sess-789", evt.SessionId);
	}

	[Fact]
	public void Deserializes_ResultEvent_WithIsError()
	{
		var json = """
		{
			"type": "result",
			"subtype": "success",
			"cost_usd": 0,
			"duration_ms": 2345,
			"duration_api_ms": 0,
			"is_error": true,
			"num_turns": 0,
			"session_id": "sess-789"
		}
		""";

		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("result", evt!.Type);
		Assert.Equal("success", evt.Subtype);
		Assert.True(evt.IsError);
		Assert.Equal(0, evt.NumTurns);
		Assert.Equal(0m, evt.CostUsd);
		Assert.Equal("sess-789", evt.SessionId);
	}

	[Fact]
	public void Deserializes_ResultEvent_WithoutIsError()
	{
		var json = """{"type":"result","cost_usd":0.15,"input_tokens":5000,"output_tokens":200}""";
		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Null(evt!.IsError);
		Assert.Equal(0.15m, evt.CostUsd);
		Assert.Equal(5000, evt.InputTokens);
		Assert.Equal(200, evt.OutputTokens);
	}

	#endregion

	#region ParseClaudeMessage helper

	[Fact]
	public void ParseClaudeMessage_ExtractsContentAndUsage()
	{
		var json = """
		{
			"type": "assistant",
			"message": {
				"id": "msg_01",
				"type": "message",
				"role": "assistant",
				"content": [{"type": "text", "text": "Hello"}],
				"model": "claude-opus-4-20250514",
				"usage": {"input_tokens": 2000, "output_tokens": 100}
			}
		}
		""";

		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);
		var msg = evt!.ParseClaudeMessage();

		Assert.NotNull(msg);
		Assert.Equal("claude-opus-4-20250514", msg!.Model);
		Assert.NotNull(msg.Usage);
		Assert.Equal(2000, msg.Usage!.InputTokens);
		Assert.Equal(100, msg.Usage.OutputTokens);
		Assert.Single(msg.Content!);
		Assert.Equal("text", msg.Content![0].Type);
		Assert.Equal("Hello", msg.Content[0].Text);
	}

	[Fact]
	public void ParseClaudeMessage_ReturnsNull_WhenMessageIsString()
	{
		var json = """{"type":"limit","message":"Premium limit reached"}""";
		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.Null(evt!.ParseClaudeMessage());
		Assert.Equal("Premium limit reached", evt.MessageText);
	}

	[Fact]
	public void ParseClaudeMessage_ReturnsNull_WhenMessageMissing()
	{
		var json = """{"type":"system","session_id":"abc"}""";
		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.Null(evt!.ParseClaudeMessage());
		Assert.Null(evt.MessageText);
	}

	#endregion

	#region Full error output parsing (three-event sequence)

	[Fact]
	public void FullErrorSequence_AllEventsDeserialize()
	{
		var lines = new[]
		{
			"""{"type":"system","subtype":"init","session_id":"sess-err","tools":[],"mcp_servers":[]}""",
			"""{"type":"assistant","message":{"id":"msg_01","type":"message","role":"assistant","content":[],"model":"<synthetic>","stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"server_tool_use":null}},"error":"invalid_request","session_id":"sess-err"}""",
			"""{"type":"result","subtype":"success","cost_usd":0,"duration_ms":2345,"duration_api_ms":0,"is_error":true,"num_turns":0,"session_id":"sess-err"}"""
		};

		// All three lines should deserialize without throwing
		var events = new List<CopilotStreamEvent>();
		foreach (var line in lines)
		{
			var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(line, JsonOptions);
			Assert.NotNull(evt);
			events.Add(evt!);
		}

		Assert.Equal(3, events.Count);

		// System init
		Assert.Equal("system", events[0].Type);
		Assert.Equal("sess-err", events[0].SessionId);

		// Assistant with error
		Assert.Equal("assistant", events[1].Type);
		Assert.Equal("invalid_request", events[1].Error);

		// Result with is_error
		Assert.Equal("result", events[2].Type);
		Assert.True(events[2].IsError);
	}

	[Fact]
	public void FullErrorSequence_DetectsSystemModel()
	{
		var json = """
		{
			"type": "assistant",
			"message": {
				"id": "msg_01",
				"type": "message",
				"role": "assistant",
				"content": [],
				"model": "<synthetic>",
				"stop_reason": "end_turn"
			},
			"error": "invalid_request"
		}
		""";

		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);
		var msg = evt!.ParseClaudeMessage();

		Assert.NotNull(msg);
		Assert.Equal("<synthetic>", msg!.Model);
		Assert.Equal("invalid_request", evt.Error);
	}

	#endregion

	#region Content field polymorphism (string vs array)

	[Fact]
	public void Content_AsString_ProvidesContentText()
	{
		var json = """{"type":"assistant","content":"Hello world"}""";
		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.Equal("Hello world", evt!.ContentText);
		Assert.Null(evt.ParseContentBlocks());
	}

	[Fact]
	public void Content_AsArray_ProvidesParseContentBlocks()
	{
		var json = """{"type":"message","content":[{"type":"text","text":"Error occurred"}],"error":"invalid_request"}""";
		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.Null(evt!.ContentText);
		var blocks = evt.ParseContentBlocks();
		Assert.NotNull(blocks);
		Assert.Single(blocks!);
		Assert.Equal("text", blocks[0].Type);
		Assert.Equal("Error occurred", blocks[0].Text);
	}

	[Fact]
	public void Content_WhenMissing_BothHelpersReturnNull()
	{
		var json = """{"type":"system","session_id":"abc"}""";
		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.Null(evt!.ContentText);
		Assert.Null(evt.ParseContentBlocks());
	}

	[Fact]
	public void RootContentArray_WithError_DeserializesSuccessfully()
	{
		// This is the exact format from the Copilot CLI when a model is unavailable:
		// content array at root level (not inside message), with error field
		var json = """
		{
			"type": "message",
			"subtype": "success",
			"content": [{"type": "text", "text": "There's an issue with the selected model. It may not exist or you may not have access to it."}],
			"error": "invalid_request",
			"session_id": "test-session"
		}
		""";

		var evt = JsonSerializer.Deserialize<CopilotStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("message", evt!.Type);
		Assert.Equal("invalid_request", evt.Error);
		Assert.Equal("test-session", evt.SessionId);

		// ContentText should be null (it's an array, not a string)
		Assert.Null(evt.ContentText);

		// ParseContentBlocks should extract the error text
		var blocks = evt.ParseContentBlocks();
		Assert.NotNull(blocks);
		Assert.Single(blocks!);
		Assert.Equal("There's an issue with the selected model. It may not exist or you may not have access to it.", blocks[0].Text);
	}

	#endregion

	#region GetDefaultModelMultiplier — matches CLI /models output

	[Theory]
	[InlineData("claude-opus-4.5", 3.0)]
	[InlineData("claude-opus-4.6", 3.0)]
	[InlineData("claude-opus-4.6-fast", 30.0)]
	[InlineData("claude-sonnet-4.5", 1.0)]
	[InlineData("claude-sonnet-4.6", 1.0)]
	[InlineData("claude-sonnet-4", 1.0)]
	[InlineData("claude-haiku-4.5", 0.33)]
	[InlineData("gpt-5.1-codex-max", 1.0)]
	[InlineData("gpt-5.1-codex", 1.0)]
	[InlineData("gpt-5-mini", 0)]
	[InlineData("gpt-5.1-codex-mini", 0.33)]
	[InlineData("gpt-4.1", 0)]
	[InlineData("gpt-5.2", 1.0)]
	[InlineData("gpt-5.4", 1.0)]
	[InlineData("gpt-5.3-codex", 1.0)]
	[InlineData("gemini-3-pro-preview", 1.0)]
	[InlineData("unknown-model", 1.0)]
	public void GetDefaultModelMultiplier_ReturnsExpectedValues(string modelId, double expectedMultiplier)
	{
		var result = CopilotProvider.GetDefaultModelMultiplier(modelId);
		Assert.Equal((decimal)expectedMultiplier, result);
	}

	#endregion

	#region CopilotModelParser — CLI model discovery

	[Fact]
	public void ParseModelChoicesFromError_ExtractsModels()
	{
		var stderr = """
		error: option '--model <model>' argument '__invalid__' is invalid. Allowed choices are claude-sonnet-4.6, claude-sonnet-4.5, claude-haiku-4.5, claude-opus-4.6, claude-opus-4.6-fast, claude-opus-4.5, claude-sonnet-4, gemini-3-pro-preview, gpt-5.4, gpt-5.3-codex, gpt-5.2-codex, gpt-5.2, gpt-5.1-codex-max, gpt-5.1-codex, gpt-5.1, gpt-5.1-codex-mini, gpt-5-mini, gpt-4.1.
		""";

		var models = CopilotModelParser.ParseModelChoicesFromError(stderr);

		Assert.NotNull(models);
		Assert.Equal(18, models!.Count);
		Assert.Contains("claude-sonnet-4.6", models);
		Assert.Contains("claude-opus-4.6-fast", models);
		Assert.Contains("gpt-5.4", models);
		Assert.Contains("gpt-4.1", models);
		Assert.Contains("gemini-3-pro-preview", models);
	}

	[Fact]
	public void ParseModelChoicesFromError_ReturnsNull_ForEmptyInput()
	{
		Assert.Null(CopilotModelParser.ParseModelChoicesFromError(null));
		Assert.Null(CopilotModelParser.ParseModelChoicesFromError(""));
		Assert.Null(CopilotModelParser.ParseModelChoicesFromError("some random error"));
	}

	[Fact]
	public void ParseModelChoicesFromError_HandlesMultilineStderr()
	{
		// Real-world stderr includes Node.js error wrapper text
		var stderr = """
		node.exe : error: option '--model <model>' argument '__invalid_model_probe__' is invalid. Allowed choices are claude-sonnet-4.6, gpt-5.4, gpt-4.1.
		At C:\Users\someone\AppData\Roaming\npm\copilot.ps1:24 char:5
		+     & "node$exe"  "$basedir/node_modules/@github/copilot/npm-loader.j ...
		+     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
		    + CategoryInfo          : NotSpecified
		""";

		var models = CopilotModelParser.ParseModelChoicesFromError(stderr);

		Assert.NotNull(models);
		Assert.Equal(3, models!.Count);
		Assert.Contains("claude-sonnet-4.6", models);
		Assert.Contains("gpt-5.4", models);
		Assert.Contains("gpt-4.1", models);
	}

	#endregion
}
