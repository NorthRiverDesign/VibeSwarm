using System.Text.Json;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Providers.Claude;
using VibeSwarm.Shared.Providers.Copilot;

namespace VibeSwarm.Tests;

/// <summary>
/// Covers the provider integration points that track upstream CLI behaviour: structured
/// usage limits, unmetered models, and the recorded version reference.
/// </summary>
public sealed class ProviderVersionAlignmentTests
{
	/// <summary>
	/// Captured verbatim from `claude -p ... --output-format stream-json` on Claude Code
	/// 2.1.276. Kept as raw JSON so a change to the CLI's shape fails here rather than
	/// silently producing empty meters.
	/// </summary>
	private const string RateLimitEventJson = """
		{
		  "type": "rate_limit_event",
		  "rate_limit_info": {
		    "status": "allowed_warning",
		    "resetsAt": 1790812800,
		    "rateLimitType": "overage",
		    "utilization": 0.93,
		    "isUsingOverage": false,
		    "surpassedThreshold": 0.75,
		    "unifiedWindows": {
		      "five_hour": { "utilization": 0.11, "resetsAt": 1789757400 },
		      "seven_day": { "utilization": 0.3, "resetsAt": 1790035200 }
		    }
		  }
		}
		""";

	private static ClaudeStreamEvent DeserializeEvent(string json)
		=> JsonSerializer.Deserialize<ClaudeStreamEvent>(
			json,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

	[Fact]
	public void ClaudeStreamEvent_DeserializesRateLimitEvent()
	{
		var evt = DeserializeEvent(RateLimitEventJson);

		Assert.Equal("rate_limit_event", evt.Type);
		Assert.NotNull(evt.RateLimitInfo);
		Assert.Equal("allowed_warning", evt.RateLimitInfo!.Status);
		Assert.Equal("overage", evt.RateLimitInfo.RateLimitType);
		Assert.Equal(0.93, evt.RateLimitInfo.Utilization);
		Assert.False(evt.RateLimitInfo.IsUsingOverage);
		Assert.Equal(0.11, evt.RateLimitInfo.UnifiedWindows!.FiveHour!.Utilization);
		Assert.Equal(0.3, evt.RateLimitInfo.UnifiedWindows.SevenDay!.Utilization);
	}

	[Fact]
	public void ParseRateLimitEvent_MapsFiveHourToSessionAndSevenDayToWeekly()
	{
		var evt = DeserializeEvent(RateLimitEventJson);

		var limits = ClaudeUsageParser.ParseRateLimitEvent(evt.RateLimitInfo);

		Assert.NotNull(limits);
		Assert.False(limits!.IsLimitReached);

		var session = Assert.Single(limits.Windows, w => w.Scope == UsageLimitWindowScope.Session);
		Assert.Equal(11, session.CurrentUsage);
		Assert.Equal(100, session.MaxUsage);
		Assert.Equal(
			DateTimeOffset.FromUnixTimeSeconds(1789757400).UtcDateTime,
			session.ResetTime);

		var weekly = Assert.Single(limits.Windows, w => w.Scope == UsageLimitWindowScope.Weekly);
		Assert.Equal(30, weekly.CurrentUsage);
		Assert.Equal(100, weekly.MaxUsage);
	}

	[Fact]
	public void ParseRateLimitEvent_SurfacesBindingLimitAsPrimaryWindow()
	{
		var evt = DeserializeEvent(RateLimitEventJson);

		var limits = ClaudeUsageParser.ParseRateLimitEvent(evt.RateLimitInfo);

		// The overage window is at 93%, well above either rolling window, so it must be the
		// figure the dashboard leads with.
		Assert.Equal(93, limits!.CurrentUsage);
		Assert.Equal(100, limits.MaxUsage);
		Assert.Contains("overage", limits.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("allowed", false)]
	[InlineData("allowed_warning", false)]
	[InlineData("rejected", true)]
	[InlineData("blocked", true)]
	public void ParseRateLimitEvent_TreatsNonAllowedStatusAsLimitReached(string status, bool expected)
	{
		var info = new ClaudeRateLimitInfo
		{
			Status = status,
			Utilization = 0.5,
			UnifiedWindows = new ClaudeUnifiedWindows
			{
				FiveHour = new ClaudeRateLimitWindow { Utilization = 0.5 }
			}
		};

		var limits = ClaudeUsageParser.ParseRateLimitEvent(info);

		Assert.Equal(expected, limits!.IsLimitReached);
	}

	[Fact]
	public void ParseRateLimitEvent_ClampsUtilizationPastTheLimit()
	{
		var info = new ClaudeRateLimitInfo
		{
			Status = "allowed_warning",
			Utilization = 1.4,
		};

		var limits = ClaudeUsageParser.ParseRateLimitEvent(info);

		Assert.Equal(100, limits!.CurrentUsage);
		Assert.True(limits.IsLimitReached);
	}

	[Fact]
	public void ParseRateLimitEvent_ReturnsNullWhenNothingReported()
	{
		Assert.Null(ClaudeUsageParser.ParseRateLimitEvent(null));
		Assert.Null(ClaudeUsageParser.ParseRateLimitEvent(new ClaudeRateLimitInfo()));
	}

	[Theory]
	[InlineData("ollama/qwen3-coder", false)]
	[InlineData("lmstudio/devstral-small", false)]
	[InlineData("llama.cpp/gpt-oss-20b", false)]
	[InlineData("anthropic/claude-opus-5", true)]
	[InlineData("openrouter/some-model", true)]
	[InlineData("gpt-5.4", true)]
	[InlineData(null, true)]
	public void ProviderMetering_OnlyExemptsKnownSelfHostedRuntimes(string? model, bool expectedMetered)
	{
		Assert.Equal(expectedMetered, ProviderMetering.IsMetered(ProviderType.OpenCode, model));
	}

	[Fact]
	public void ProviderMetering_UnmeteredLimitsAreNeverReached()
	{
		var limits = ProviderMetering.CreateUnmeteredLimits("No usage limits. Model runs locally via ollama.");

		Assert.Equal(UsageLimitType.Unmetered, limits.LimitType);
		Assert.False(limits.IsLimitReached);
		Assert.Empty(limits.Windows);
		Assert.True(ProviderMetering.IsUnmetered(limits.LimitType));
		Assert.False(ProviderMetering.IsUnmetered(UsageLimitType.None));
	}

	/// <summary>
	/// Captured verbatim from `copilot -p ... --usage-output-file` on Copilot CLI 1.0.86.
	/// GitHub publishes no schema for this file, so this sample is the contract: if the
	/// shape changes upstream, this test is what notices.
	/// </summary>
	private const string CopilotUsageReportJson = """
		{
		  "totalPremiumRequestCost": 1,
		  "totalUserRequests": 1,
		  "totalNanoAiu": 1928450000,
		  "tokenDetails": {
		    "input": { "tokenCount": 2 },
		    "cache_read": { "tokenCount": 14465 },
		    "cache_write": { "tokenCount": 6539 },
		    "output": { "tokenCount": 4 }
		  },
		  "totalApiDurationMs": 716,
		  "sessionStartTime": "2026-09-18T17:13:40.349Z",
		  "codeChanges": { "linesAdded": 0, "linesRemoved": 0, "filesModifiedCount": 0 },
		  "modelMetrics": {
		    "claude-sonnet-5": {
		      "requests": { "count": 1, "cost": 1 },
		      "usage": {
		        "inputTokens": 21006, "outputTokens": 4,
		        "cacheReadTokens": 14465, "cacheWriteTokens": 6539, "reasoningTokens": 0
		      }
		    }
		  }
		}
		""";

	private static string WriteTempReport(string json)
	{
		var path = Path.Combine(Path.GetTempPath(), $"vibeswarm-copilot-usage-{Guid.NewGuid():N}.json");
		File.WriteAllText(path, json);
		return path;
	}

	[Fact]
	public void CopilotUsageFileReader_ReadsTheRealReportShape()
	{
		var path = WriteTempReport(CopilotUsageReportJson);

		try
		{
			var result = new ExecutionResult();

			Assert.True(CopilotUsageFileReader.TryApply(path, result));

			// Input is the sum of uncached input, cache reads and cache writes, which is
			// what the per-model block independently reports as inputTokens (21006).
			Assert.Equal(21_006, result.InputTokens);
			Assert.Equal(4, result.OutputTokens);
			Assert.False(result.IsTokenEstimate);
			Assert.Equal(1, result.PremiumRequestsConsumed);
			Assert.Equal("claude-sonnet-5", result.ModelUsed);

			// 1,928,450,000 nano-AIU is 1.92845 AI Units.
			Assert.Equal(1.92845m, result.AiCreditsConsumed);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void CopilotUsageFileReader_PicksTheModelServingMostRequests()
	{
		var path = WriteTempReport("""
			{
			  "modelMetrics": {
			    "gpt-5.4": { "requests": { "count": 2, "cost": 2 } },
			    "claude-sonnet-5": { "requests": { "count": 9, "cost": 9 } }
			  }
			}
			""");

		try
		{
			Assert.Equal("claude-sonnet-5", CopilotUsageFileReader.Read(path)!.Model);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void CopilotUsageFileReader_IgnoresMissingAndUnrecognizedFiles()
	{
		Assert.Null(CopilotUsageFileReader.Read(null));
		Assert.Null(CopilotUsageFileReader.Read("/nonexistent/vibeswarm-usage.json"));

		// Unrecognised content must leave the caller's stderr-derived values untouched.
		var path = WriteTempReport("""{ "somethingElse": "entirely" }""");
		try
		{
			Assert.False(CopilotUsageFileReader.TryApply(path, new ExecutionResult()));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ProviderVersionReference_RecordsATargetForEveryProviderType()
	{
		foreach (var providerType in Enum.GetValues<ProviderType>())
		{
			var target = ProviderVersionReference.For(providerType);

			Assert.Equal(providerType, target.Type);
			Assert.Equal(ProviderCapabilities.GetDefaultExecutable(providerType), target.Executable);
			Assert.True(
				target.MinimumSupported <= target.VerifiedAgainst,
				$"{providerType}: minimum {target.MinimumSupported} exceeds verified {target.VerifiedAgainst}");
			Assert.NotEmpty(target.Notes);
		}
	}

	[Theory]
	[InlineData(null, VersionSupportState.Unknown)]
	[InlineData("1.9.9", VersionSupportState.Unsupported)]
	[InlineData("2.1.108", VersionSupportState.Older)]
	[InlineData("2.1.276", VersionSupportState.Verified)]
	[InlineData("2.2.0", VersionSupportState.Newer)]
	public void ProviderVersionReference_EvaluatesInstalledVersionAgainstTarget(string? version, VersionSupportState expected)
	{
		var detected = version == null ? null : Version.Parse(version);

		Assert.Equal(expected, ProviderVersionReference.Evaluate(ProviderType.Claude, detected));
	}

	[Theory]
	// Exact --version output from each CLI at the verified release.
	[InlineData("2.1.276 (Claude Code)", "2.1.276")]
	[InlineData("GitHub Copilot CLI 1.0.86.", "1.0.86")]
	[InlineData("1.18.31", "1.18.31")]
	[InlineData("v1.2.3-beta.1", "1.2.3")]
	[InlineData("no digits here", null)]
	[InlineData("", null)]
	[InlineData(null, null)]
	public void ProviderVersionReference_ParsesEachCliVersionFormat(string? raw, string? expected)
	{
		var parsed = ProviderVersionReference.TryParseVersion(raw, out var version);

		if (expected == null)
		{
			Assert.False(parsed);
			Assert.Null(version);
			return;
		}

		Assert.True(parsed);
		Assert.Equal(Version.Parse(expected), version);
	}

	[Fact]
	public void ProviderVersionReference_DescribesWhyFeaturesMayBeUnavailable()
	{
		var older = ProviderVersionReference.Describe(ProviderType.Claude, new Version(2, 1, 108));

		Assert.Contains("2.1.108", older);
		Assert.Contains("2.1.276", older);
	}

	[Fact]
	public void ProviderCapabilities_AdvertiseTheEffortLevelsTheVerifiedCliAccepts()
	{
		var copilot = ProviderCapabilities.GetSupportedReasoningEfforts(
			ProviderType.Copilot,
			ProviderConnectionMode.CLI);

		// Copilot 1.0.86: none, minimal, low, medium, high, xhigh, max.
		Assert.Equal(["none", "minimal", "low", "medium", "high", "xhigh", "max"], copilot);
	}
}
