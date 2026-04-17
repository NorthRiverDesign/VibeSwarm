using System.Text.Json;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Providers.Claude;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class SystemErrorDetectionTests
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
	};

	#region ClaudeStreamEvent deserialization

	[Fact]
	public void ClaudeStreamEvent_Deserializes_ErrorField()
	{
		var json = """{"type":"assistant","error":"invalid_request"}""";
		var evt = JsonSerializer.Deserialize<ClaudeStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("assistant", evt!.Type);
		Assert.Equal("invalid_request", evt.Error);
	}

	[Fact]
	public void ClaudeStreamEvent_Deserializes_IsErrorField()
	{
		var json = """{"type":"result","subtype":"success","is_error":true,"duration_ms":433,"total_cost_usd":0}""";
		var evt = JsonSerializer.Deserialize<ClaudeStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("result", evt!.Type);
		Assert.True(evt.IsError);
		Assert.Equal(0m, evt.TotalCostUsd);
	}

	[Fact]
	public void ClaudeStreamEvent_Deserializes_StopReason()
	{
		var json = """{"type":"result","stop_reason":"stop_sequence"}""";
		var evt = JsonSerializer.Deserialize<ClaudeStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("stop_sequence", evt!.StopReason);
	}

	[Fact]
	public void ClaudeStreamEvent_NormalResult_HasNoError()
	{
		var json = """{"type":"result","subtype":"success","is_error":false,"total_cost_usd":0.05,"input_tokens":1000,"output_tokens":500}""";
		var evt = JsonSerializer.Deserialize<ClaudeStreamEvent>(json, JsonOptions);

		Assert.NotNull(evt);
		Assert.False(evt!.IsError);
		Assert.Null(evt.Error);
		Assert.Equal(0.05m, evt.TotalCostUsd);
	}

	#endregion

	#region Full error output parsing (end-to-end)

	[Fact]
	public void ClaudeStreamEvent_Deserializes_FullSystemErrorOutput()
	{
		// This is the exact assistant event from the user's error report
		var assistantJson = """{"type":"assistant","message":{"id":"a38e0e7e-3bd1-4af7-97fe-c780a0647058","container":null,"model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":null,"cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":0},"inference_geo":null,"iterations":null,"speed":null},"content":[{"type":"text","text":"There's an issue with the selected model (claude-opus-4-6-20260101). It may not exist or you may not have access to it. Run --model to pick a different model."}],"context_management":null},"parent_tool_use_id":null,"session_id":"8067c79f-135c-467c-9315-4d6c947cef6c","uuid":"139fe54d-d23c-4e98-bdf1-cabb1adcd78a","error":"invalid_request"}""";

		var evt = JsonSerializer.Deserialize<ClaudeStreamEvent>(assistantJson, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("assistant", evt!.Type);
		Assert.Equal("invalid_request", evt.Error);
		Assert.Equal("<synthetic>", evt.Message?.Model);
		Assert.Equal(0, evt.Message?.Usage?.InputTokens);
		Assert.Equal(0, evt.Message?.Usage?.OutputTokens);

		var textContent = evt.Message?.Content?.FirstOrDefault(c => c.Type == "text");
		Assert.NotNull(textContent);
		Assert.Contains("issue with the selected model", textContent!.Text);
	}

	[Fact]
	public void ClaudeStreamEvent_Deserializes_FullResultErrorEvent()
	{
		// This is the exact result event from the user's error report
		var resultJson = """{"type":"result","subtype":"success","is_error":true,"duration_ms":433,"duration_api_ms":0,"num_turns":1,"result":"There's an issue with the selected model (claude-opus-4-6-20260101). It may not exist or you may not have access to it. Run --model to pick a different model.","stop_reason":"stop_sequence","session_id":"8067c79f-135c-467c-9315-4d6c947cef6c","total_cost_usd":0,"usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard","cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":0},"inference_geo":"","iterations":[],"speed":"standard"},"modelUsage":{},"permission_denials":[],"fast_mode_state":"off","uuid":"e9ca3236-29c5-47de-b3f3-e3a6f6da7258"}""";

		var evt = JsonSerializer.Deserialize<ClaudeStreamEvent>(resultJson, JsonOptions);

		Assert.NotNull(evt);
		Assert.Equal("result", evt!.Type);
		Assert.True(evt.IsError);
		Assert.Equal(0m, evt.TotalCostUsd);
		Assert.Equal(0, evt.Usage?.InputTokens);
		Assert.Equal(0, evt.Usage?.OutputTokens);
		Assert.Contains("issue with the selected model", evt.Result);
	}

	#endregion

	#region ProviderHealthTracker.RecordSystemFailure

	[Fact]
	public void RecordSystemFailure_ImmediatelyOpensCircuit()
	{
		var tracker = new ProviderHealthTracker();
		var providerId = Guid.NewGuid();

		// Verify circuit starts closed
		var healthBefore = tracker.GetProviderHealth(providerId);
		Assert.Equal(CircuitState.Closed, healthBefore.CircuitState);

		// A single system failure should immediately open the circuit
		tracker.RecordSystemFailure(providerId, "Model unavailable");

		var healthAfter = tracker.GetProviderHealth(providerId);
		Assert.Equal(CircuitState.Open, healthAfter.CircuitState);
		Assert.False(healthAfter.IsHealthy);
		Assert.Equal("Model unavailable", healthAfter.LastError);
	}

	[Fact]
	public void RecordSystemFailure_UsesLongerResetTimeout()
	{
		var tracker = new ProviderHealthTracker
		{
			CircuitResetTimeout = TimeSpan.FromMinutes(2),
			SystemFailureResetTimeout = TimeSpan.FromMinutes(5)
		};
		var providerId = Guid.NewGuid();

		tracker.RecordSystemFailure(providerId, "Upstream outage");

		// Circuit should still be open (system failure uses 5-minute timeout, not 2-minute)
		var health = tracker.GetProviderHealth(providerId);
		Assert.Equal(CircuitState.Open, health.CircuitState);
	}

	[Fact]
	public void RecordSystemFailure_SuccessesCloseCircuitAndClearFlag()
	{
		var tracker = new ProviderHealthTracker
		{
			CircuitResetTimeout = TimeSpan.Zero,
			SystemFailureResetTimeout = TimeSpan.Zero,
			SuccessThreshold = 1
		};
		var providerId = Guid.NewGuid();

		tracker.RecordSystemFailure(providerId, "Upstream outage");

		// With zero timeout, GetProviderHealth evaluates the circuit and moves it to HalfOpen
		var health = tracker.GetProviderHealth(providerId);
		Assert.Equal(CircuitState.HalfOpen, health.CircuitState);

		// Success should close the circuit
		tracker.RecordSuccess(providerId);
		health = tracker.GetProviderHealth(providerId);
		Assert.Equal(CircuitState.Closed, health.CircuitState);
		Assert.True(health.IsHealthy);
	}

	[Fact]
	public void NormalFailure_DoesNotImmediatelyOpenCircuit()
	{
		var tracker = new ProviderHealthTracker { FailureThreshold = 5 };
		var providerId = Guid.NewGuid();

		// A single normal failure should NOT open the circuit
		tracker.RecordFailure(providerId, "Task failed");

		var health = tracker.GetProviderHealth(providerId);
		Assert.Equal(CircuitState.Closed, health.CircuitState);
		Assert.True(health.IsHealthy);
	}

	[Fact]
	public void RecordRateLimitFailure_EscalatesCooldownToThreeMinutes()
	{
		var tracker = new ProviderHealthTracker();
		var providerId = Guid.NewGuid();

		tracker.RecordRateLimitFailure(providerId, "first");
		var firstCooldown = tracker.GetProviderHealth(providerId).RateLimitResetTime;

		tracker.RecordRateLimitFailure(providerId, "second");
		var secondCooldown = tracker.GetProviderHealth(providerId).RateLimitResetTime;

		tracker.RecordRateLimitFailure(providerId, "third");
		var thirdCooldown = tracker.GetProviderHealth(providerId).RateLimitResetTime;

		tracker.RecordRateLimitFailure(providerId, "fourth");
		var fourthCooldown = tracker.GetProviderHealth(providerId).RateLimitResetTime;

		Assert.NotNull(firstCooldown);
		Assert.NotNull(secondCooldown);
		Assert.NotNull(thirdCooldown);
		Assert.NotNull(fourthCooldown);
		Assert.True(secondCooldown > firstCooldown);
		Assert.True(thirdCooldown > secondCooldown);
		Assert.True(fourthCooldown <= thirdCooldown!.Value.AddSeconds(2));
	}

	[Fact]
	public void RecordRateLimitFailure_UsesProviderResetWhenItIsLonger()
	{
		var tracker = new ProviderHealthTracker();
		var providerId = Guid.NewGuid();
		var providerResetTime = DateTime.UtcNow.AddMinutes(7);

		tracker.RecordRateLimitFailure(providerId, "copilot cooldown", providerResetTime);
		var health = tracker.GetProviderHealth(providerId);

		Assert.True(health.IsRateLimited);
		Assert.Equal(providerResetTime, health.RateLimitResetTime);
	}

	[Fact]
	public void RecordSuccess_ClearsEscalatedRateLimitBackoff()
	{
		var tracker = new ProviderHealthTracker();
		var providerId = Guid.NewGuid();

		tracker.RecordRateLimitFailure(providerId, "first");
		tracker.RecordSuccess(providerId);
		tracker.RecordRateLimitFailure(providerId, "after success");
		var health = tracker.GetProviderHealth(providerId);

		Assert.True(health.IsRateLimited);
		Assert.NotNull(health.RateLimitResetTime);
		Assert.True(health.RateLimitResetTime <= DateTime.UtcNow.AddMinutes(1).AddSeconds(2));
	}

	#endregion

	#region Copilot rate limit parsing

	[Fact]
	public void CopilotUsageParser_TryAgainInOneMinute_IsRateLimit()
	{
		var result = new ExecutionResult
		{
			Success = false,
			ErrorMessage = "Request failed. Please try again in 1 minute."
		};

		CopilotUsageParser.ApplyToExecutionResult(result.ErrorMessage!, result);

		Assert.NotNull(result.DetectedUsageLimits);
		Assert.True(result.DetectedUsageLimits!.IsLimitReached);
		Assert.Equal(UsageLimitType.RateLimit, result.DetectedUsageLimits.LimitType);
		Assert.NotNull(result.DetectedUsageLimits.ResetTime);
		Assert.True(result.DetectedUsageLimits.ResetTime > DateTime.UtcNow.AddSeconds(30));
	}

	#endregion

	#region ExecutionResult.IsSystemError

	[Fact]
	public void ExecutionResult_IsSystemError_DefaultsFalse()
	{
		var result = new ExecutionResult();
		Assert.False(result.IsSystemError);
	}

	[Fact]
	public void ExecutionResult_IsSystemError_CanBeSet()
	{
		var result = new ExecutionResult { IsSystemError = true };
		Assert.True(result.IsSystemError);
	}

	#endregion
}
