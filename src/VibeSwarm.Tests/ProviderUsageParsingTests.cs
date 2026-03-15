using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class ProviderUsageParsingTests
{
	[Fact]
	public void CopilotUsageParser_ApplyToExecutionResult_ParsesTokensPremiumRequestsAndBudget()
	{
		var stderr = """
			[ERR] gpt-5.4 49.0k in, 301 out, 32.0k cached (Est. 3 Premium requests)
			[ERR] Premium requests used: 42/300
			""";
		var result = new ExecutionResult();

		CopilotUsageParser.ApplyToExecutionResult(stderr, result);

		Assert.Equal(49_000, result.InputTokens);
		Assert.Equal(301, result.OutputTokens);
		Assert.Equal(3, result.PremiumRequestsConsumed);
		Assert.Equal("gpt-5.4", result.ModelUsed);
		Assert.NotNull(result.DetectedUsageLimits);
		Assert.Equal(UsageLimitType.PremiumRequests, result.DetectedUsageLimits!.LimitType);
		Assert.Equal(42, result.DetectedUsageLimits.CurrentUsage);
		Assert.Equal(300, result.DetectedUsageLimits.MaxUsage);
	}

	[Fact]
	public void ClaudeUsageParser_ParseLimitSignals_ParsesWeeklyLimitFractionAndReset()
	{
		var stderr = "Warning: Weekly limit 72/100 used. Try again in 3 hours.";

		var limits = ClaudeUsageParser.ParseLimitSignals(stderr);

		Assert.NotNull(limits);
		Assert.Equal(UsageLimitType.RateLimit, limits!.LimitType);
		Assert.Equal(72, limits.CurrentUsage);
		Assert.Equal(100, limits.MaxUsage);
		Assert.False(limits.IsLimitReached);
		Assert.True(limits.ResetTime.HasValue);
		Assert.InRange(limits.ResetTime!.Value, DateTime.UtcNow.AddHours(2.9), DateTime.UtcNow.AddHours(3.1));
	}

	[Fact]
	public void ClaudeUsageParser_ParseLimitSignals_ParsesRemainingSessionPercentage()
	{
		var stderr = "Heads up: session limit 25% remaining before you need to wait.";

		var limits = ClaudeUsageParser.ParseLimitSignals(stderr);

		Assert.NotNull(limits);
		Assert.Equal(UsageLimitType.SessionLimit, limits!.LimitType);
		Assert.Equal(75, limits.CurrentUsage);
		Assert.Equal(100, limits.MaxUsage);
	}
}
