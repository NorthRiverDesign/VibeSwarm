using System.Text.Json;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Providers.Claude;

namespace VibeSwarm.Tests;

/// <summary>
/// The set of windows Claude reports is not fixed — it varies by model and plan. These
/// payloads were captured from Claude Code 2.1.277 on 2026-09-18, so a change upstream
/// fails here rather than silently dropping a meter.
/// </summary>
public sealed class ClaudeUsageWindowTests
{
	/// <summary>Captured from a Haiku run: two windows.</summary>
	private const string HaikuEventJson = """
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
		      "five_hour": { "utilization": 0.1, "resetsAt": 1789775400 },
		      "seven_day": { "utilization": 0.34, "resetsAt": 1790035200 }
		    }
		  }
		}
		""";

	/// <summary>Captured from a Fable run on the same account moments later: three windows.</summary>
	private const string FableEventJson = """
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
		      "five_hour": { "utilization": 0.1, "resetsAt": 1789775400 },
		      "seven_day": { "utilization": 0.34, "resetsAt": 1790035200 },
		      "seven_day_overage_included": { "utilization": 0, "resetsAt": 1790035200 }
		    }
		  }
		}
		""";

	/// <summary>The same shape once the included allowance runs out and billing starts.</summary>
	private const string OverageInUseEventJson = """
		{
		  "type": "rate_limit_event",
		  "rate_limit_info": {
		    "status": "allowed",
		    "resetsAt": 1790812800,
		    "rateLimitType": "overage",
		    "utilization": 0.42,
		    "isUsingOverage": true,
		    "unifiedWindows": {
		      "five_hour": { "utilization": 0.1, "resetsAt": 1789775400 }
		    }
		  }
		}
		""";

	private static ClaudeRateLimitInfo Parse(string json)
		=> JsonSerializer.Deserialize<ClaudeStreamEvent>(
			json,
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.RateLimitInfo!;

	[Fact]
	public void FableRun_KeepsTheOverageWindowTheOldParserDropped()
	{
		var limits = ClaudeUsageParser.ParseRateLimitEvent(Parse(FableEventJson));

		Assert.NotNull(limits);

		// Three reported windows plus the binding-limit summary window.
		Assert.Equal(4, limits!.Windows.Count);
		Assert.Contains(limits.Windows, w => w.Label == "Session (5 hours)");
		Assert.Contains(limits.Windows, w => w.Label == "Weekly");
		Assert.Contains(limits.Windows, w => w.Label == "Weekly (with overage)");
	}

	[Fact]
	public void HaikuRun_ReportsOnlyTheWindowsItActuallyCarries()
	{
		var limits = ClaudeUsageParser.ParseRateLimitEvent(Parse(HaikuEventJson));

		Assert.NotNull(limits);
		Assert.DoesNotContain(limits!.Windows, w => w.Label == "Weekly (with overage)");
	}

	[Fact]
	public void WindowsCarryUtilizationAsAPercentageAndTheirResetTime()
	{
		var limits = ClaudeUsageParser.ParseRateLimitEvent(Parse(FableEventJson));

		var session = Assert.Single(limits!.Windows, w => w.Label == "Session (5 hours)");
		Assert.Equal(10, session.CurrentUsage);
		Assert.Equal(100, session.MaxUsage);
		Assert.Equal(UsageLimitWindowScope.Session, session.Scope);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789775400).UtcDateTime, session.ResetTime);

		var weekly = Assert.Single(limits.Windows, w => w.Label == "Weekly");
		Assert.Equal(34, weekly.CurrentUsage);
		Assert.Equal(UsageLimitWindowScope.Weekly, weekly.Scope);
	}

	[Fact]
	public void AWindowNobodyHasSeenBefore_StillRendersWithASensibleLabel()
	{
		// Guards the reason this is a dictionary: a new key must survive rather than vanish.
		var info = new ClaudeRateLimitInfo
		{
			Status = "allowed",
			UnifiedWindows = new Dictionary<string, ClaudeRateLimitWindow>
			{
				["thirty_day_opus"] = new() { Utilization = 0.42 }
			}
		};

		var limits = ClaudeUsageParser.ParseRateLimitEvent(info);

		var window = Assert.Single(limits!.Windows);
		Assert.Equal("Thirty day opus", window.Label);
		Assert.Equal(42, window.CurrentUsage);
		Assert.Equal(UsageLimitWindowScope.Monthly, window.Scope);
	}

	[Theory]
	[InlineData("five_hour", UsageLimitWindowScope.Session)]
	[InlineData("seven_day", UsageLimitWindowScope.Weekly)]
	[InlineData("seven_day_overage_included", UsageLimitWindowScope.Weekly)]
	[InlineData("some_weekly_thing", UsageLimitWindowScope.Weekly)]
	[InlineData("two_hour_burst", UsageLimitWindowScope.Session)]
	[InlineData("monthly_spend", UsageLimitWindowScope.Monthly)]
	public void WindowKeysMapToTheExpectedTimeHorizon(string key, UsageLimitWindowScope expected)
	{
		var info = new ClaudeRateLimitInfo
		{
			Status = "allowed",
			UnifiedWindows = new Dictionary<string, ClaudeRateLimitWindow>
			{
				[key] = new() { Utilization = 0.5 }
			}
		};

		var limits = ClaudeUsageParser.ParseRateLimitEvent(info);

		Assert.Equal(expected, Assert.Single(limits!.Windows).Scope);
	}

	[Fact]
	public void NoWindowsAndNoBindingFigure_ProducesNothingRatherThanAnEmptyMeter()
	{
		var limits = ClaudeUsageParser.ParseRateLimitEvent(new ClaudeRateLimitInfo { Status = "allowed" });

		Assert.Null(limits);
	}

	[Fact]
	public void DrawingOnOverage_CountsAsLimitReached_EvenWhileStillAllowed()
	{
		// The CLI keeps saying "allowed" while it bills the overage balance. Treating that
		// as headroom is how an unattended queue runs up a bill, so it has to read as the
		// limit being reached and push the job to the next provider.
		var limits = ClaudeUsageParser.ParseRateLimitEvent(Parse(OverageInUseEventJson));

		Assert.NotNull(limits);
		Assert.True(limits!.IsLimitReached);
	}

	[Fact]
	public void IncludedUsageBelowTheLimit_IsNotTreatedAsReached()
	{
		// 93% of the included allowance with no overage drawn is still usable.
		var limits = ClaudeUsageParser.ParseRateLimitEvent(Parse(HaikuEventJson));

		Assert.NotNull(limits);
		Assert.False(limits!.IsLimitReached);
	}
}
