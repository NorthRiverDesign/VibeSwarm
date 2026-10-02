using System.Text;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

/// <summary>
/// Feeds <see cref="CopilotProvider"/> the JSONL that <c>copilot -p ... --output-format json</c>
/// actually prints. The lines are trimmed from a real Copilot CLI 1.0.91 run (long opaque
/// fields removed); no other fields were renamed or invented.
/// </summary>
public sealed class CopilotSessionEventTests
{
	private const string SessionId = "d968b251-2c65-43a5-ad9b-bfb1a5e9b985";

	private static readonly string[] CapturedRun =
	[
		"""{"type":"session.mcp_server_status_changed","data":{"serverName":"github-mcp-server","status":"connected"},"ephemeral":true,"id":"a1","timestamp":"2026-10-01T21:40:57.900Z","parentId":null}""",
		"""{"type":"user.message","data":{"content":"Reply with the single word ok.","transformedContent":"<current_datetime>2026-10-01T17:40:58.017-04:00</current_datetime>"},"id":"a2","timestamp":"2026-10-01T21:40:58.017Z","parentId":"a1"}""",
		"""{"type":"assistant.turn_start","data":{"turnId":"0","interactionId":"61ec77fc-796b-47a8-8913-c56e8b31ef9c"},"id":"a3","timestamp":"2026-10-01T21:40:58.020Z","parentId":"a2"}""",
		"""{"type":"assistant.message_delta","data":{"messageId":"6a7a5d04-1e63-496a-8ac5-cdec63e46c7d","deltaContent":"ok"},"ephemeral":true,"id":"a4","timestamp":"2026-10-01T21:41:04.400Z","parentId":"a3"}""",
		"""{"type":"assistant.message","data":{"messageId":"6a7a5d04-1e63-496a-8ac5-cdec63e46c7d","model":"gpt-5-mini","content":"ok","toolRequests":[],"turnId":"0"},"id":"a5","timestamp":"2026-10-01T21:41:04.520Z","parentId":"a3"}""",
		"""{"type":"assistant.reasoning","data":{"reasoningId":"r1","content":"","rte":true},"ephemeral":true,"id":"a6","timestamp":"2026-10-01T21:41:04.523Z","parentId":"a5"}""",
		"""{"type":"tool.execution_start","data":{"toolCallId":"call_EKRt6CPExfaxvny8qqaV37en","toolName":"task_complete","arguments":{"summary":"Replied with ok."},"turnId":"0","model":"gpt-5-mini","toolTitle":"Task complete"},"id":"a7","timestamp":"2026-10-01T21:41:10.251Z","parentId":"a6"}""",
		"""{"type":"tool.execution_complete","data":{"toolCallId":"call_EKRt6CPExfaxvny8qqaV37en","model":"gpt-5-mini","turnId":"0","rte":true,"success":true,"result":{"content":"Replied with ok.","detailedContent":"Task completed: Replied with ok."},"toolTelemetry":{}},"id":"a8","timestamp":"2026-10-01T21:41:10.255Z","parentId":"a7"}""",
		"""{"type":"session.task_complete","data":{"summary":"Replied with ok.","success":true},"id":"a9","timestamp":"2026-10-01T21:41:10.257Z","parentId":"a8"}""",
		"""{"type":"session.usage_checkpoint","data":{"totalNanoAiu":829695000,"totalPremiumRequests":0},"id":"a10","timestamp":"2026-10-01T21:41:10.260Z","parentId":"a9"}""",
		"""{"type":"assistant.idle","data":{},"ephemeral":true,"id":"a11","timestamp":"2026-10-01T21:41:10.262Z","parentId":"a10"}""",
		"""{"type":"result","timestamp":"2026-10-01T21:41:10.274Z","sessionId":"d968b251-2c65-43a5-ad9b-bfb1a5e9b985","exitCode":0,"usage":{"premiumRequests":0,"totalApiDurationMs":12080,"sessionDurationMs":13033,"codeChanges":{"linesAdded":0,"linesRemoved":0,"filesModified":[]}}}""",
	];

	[Fact]
	public void CapturedRun_YieldsSessionModelMessagesAndTools()
	{
		var (result, currentMessage) = Replay(CapturedRun);

		Assert.Equal(SessionId, result.SessionId);
		Assert.Equal("gpt-5-mini", result.ModelUsed);
		Assert.Equal(0, result.PremiumRequestsConsumed);
		Assert.False(result.IsSystemError);

		// The assistant text is recorded once, from assistant.message rather than its delta.
		var assistant = Assert.Single(result.Messages, m => m.Role == "assistant");
		Assert.Equal("ok", assistant.Content);
		Assert.Equal(0, currentMessage.Length);

		var toolUse = Assert.Single(result.Messages, m => m.Role == "tool_use");
		Assert.Equal("task_complete", toolUse.ToolName);
		Assert.Contains("Replied with ok.", toolUse.ToolInput);

		var toolResult = Assert.Single(result.Messages, m => m.Role == "tool_result");
		Assert.Equal("task_complete", toolResult.ToolName);
		Assert.Equal("Replied with ok.", toolResult.ToolOutput);
	}

	[Fact]
	public void FailedToolExecution_IsRecordedAsToolError()
	{
		var (result, _) = Replay(
			"""{"type":"tool.execution_start","data":{"toolCallId":"c1","toolName":"bash","arguments":{"command":"false"}},"id":"b1"}""",
			"""{"type":"tool.execution_complete","data":{"toolCallId":"c1","success":false,"error":{"message":"exit code 1"}},"id":"b2"}""");

		var toolError = Assert.Single(result.Messages, m => m.Role == "tool_error");
		Assert.Equal("bash", toolError.ToolName);
		Assert.Equal("exit code 1", toolError.ToolOutput);
	}

	[Fact]
	public void SessionError_SetsErrorMessage()
	{
		var (result, _) = Replay(
			"""{"type":"session.error","data":{"errorType":"model","message":"Model not available"},"id":"e1"}""");

		Assert.Equal("Model not available", result.ErrorMessage);
		Assert.Contains(result.Messages, m => m.Role == "system" && m.Content.Contains("Model not available"));
	}

	private static (ExecutionResult Result, StringBuilder CurrentMessage) Replay(params string[] lines)
	{
		var provider = new CopilotProvider(new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI
		});
		var result = new ExecutionResult { Messages = [] };
		var currentMessage = new StringBuilder();
		var toolNamesById = new Dictionary<string, string>();

		foreach (var line in lines)
		{
			var evt = CopilotProvider.ParseStreamEvent(line);
			Assert.NotNull(evt);
			provider.ProcessStreamEvent(evt, result, currentMessage, null, toolNamesById);
		}

		return (result, currentMessage);
	}
}
