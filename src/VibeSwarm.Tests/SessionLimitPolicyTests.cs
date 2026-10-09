using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class SessionLimitPolicyTests
{
	private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
	private static readonly DateTime SessionReset = Now.AddHours(2);

	[Fact]
	public void Evaluate_HoldsUntilResetOnceUsageReachesThreshold()
	{
		var hold = SessionLimitPolicy.Evaluate([Session(92)], 90, expectedJobUsagePercent: null, Now);

		Assert.NotNull(hold);
		Assert.Equal(SessionReset, hold!.Until);
		Assert.Equal(92, hold.PercentUsed);
		Assert.False(hold.NextJobWouldReachLimit);
	}

	[Fact]
	public void Evaluate_LetsJobsStartBelowThreshold()
	{
		Assert.Null(SessionLimitPolicy.Evaluate([Session(89)], 90, expectedJobUsagePercent: null, Now));
	}

	[Fact]
	public void Evaluate_HoldsEarlyWhenTheNextJobWouldReachTheLimit()
	{
		var hold = SessionLimitPolicy.Evaluate([Session(70)], 90, expectedJobUsagePercent: 35, Now);

		Assert.NotNull(hold);
		Assert.True(hold!.NextJobWouldReachLimit);
		Assert.Contains("about 35%", hold.Describe("Claude"));
	}

	[Fact]
	public void Evaluate_LetsJobsStartWhenTheNextJobFits()
	{
		Assert.Null(SessionLimitPolicy.Evaluate([Session(70)], 90, expectedJobUsagePercent: 20, Now));
	}

	[Fact]
	public void Evaluate_EstimateNeverHoldsASessionLessThanHalfUsed()
	{
		// Jobs that use more than a whole session would otherwise never start.
		Assert.Null(SessionLimitPolicy.Evaluate([Session(40)], 90, expectedJobUsagePercent: 120, Now));
	}

	[Fact]
	public void Evaluate_IgnoresASessionThatHasAlreadyReset()
	{
		Assert.Null(SessionLimitPolicy.Evaluate([Session(99, resetTime: Now.AddMinutes(-1))], 90, null, Now));
	}

	[Fact]
	public void Evaluate_IgnoresWindowsThatAreNotSessions()
	{
		var weekly = new UsageLimitWindow
		{
			Scope = UsageLimitWindowScope.Weekly,
			CurrentUsage = 95,
			MaxUsage = 100,
			ResetTime = Now.AddDays(3)
		};

		Assert.Null(SessionLimitPolicy.Evaluate([weekly], 90, null, Now));
	}

	[Fact]
	public void Evaluate_HoldsAReachedSessionEvenAtFullThreshold()
	{
		var reached = new UsageLimitWindow
		{
			Scope = UsageLimitWindowScope.Session,
			IsLimitReached = true,
			ResetTime = SessionReset
		};

		var hold = SessionLimitPolicy.Evaluate([reached], 100, null, Now);

		Assert.NotNull(hold);
		Assert.Equal(100, hold!.PercentUsed);
	}

	[Theory]
	[InlineData(null, 90)]
	[InlineData(75, 75)]
	[InlineData(10, 50)]
	[InlineData(150, 100)]
	public void ResolvePauseThreshold_DefaultsAndClamps(int? configured, int expected)
	{
		Assert.Equal(expected, SessionLimitPolicy.ResolvePauseThreshold(configured));
	}

	[Fact]
	public void EstimateJobUsagePercent_AveragesIncreasesWithinOneSession()
	{
		var estimate = SessionLimitPolicy.EstimateJobUsagePercent(
		[
			[Session(60)],
			[Session(45)],
			[Session(25)]
		]);

		// (15 + 20) / 2, rounded up.
		Assert.Equal(18, estimate);
	}

	[Fact]
	public void EstimateJobUsagePercent_SkipsPairsThatSpanASessionReset()
	{
		var estimate = SessionLimitPolicy.EstimateJobUsagePercent(
		[
			[Session(10)],
			[Session(95, resetTime: SessionReset.AddHours(-5))],
			[Session(80, resetTime: SessionReset.AddHours(-5))]
		]);

		Assert.Equal(15, estimate);
	}

	[Fact]
	public void EstimateJobUsagePercent_ReturnsNullWithoutComparableJobs()
	{
		Assert.Null(SessionLimitPolicy.EstimateJobUsagePercent([[Session(40)], []]));
	}

	private static UsageLimitWindow Session(int percentUsed, DateTime? resetTime = null) => new()
	{
		Scope = UsageLimitWindowScope.Session,
		LimitType = UsageLimitType.SessionLimit,
		Label = "Session (5 hours)",
		CurrentUsage = percentUsed,
		MaxUsage = 100,
		ResetTime = resetTime ?? SessionReset
	};
}
