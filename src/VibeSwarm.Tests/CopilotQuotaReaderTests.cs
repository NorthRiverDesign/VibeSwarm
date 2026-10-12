using GitHub.Copilot.Rpc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Web.Providers.Copilot;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

#pragma warning disable GHCP001 // The account quota types are marked experimental in the SDK
public sealed class CopilotQuotaReaderTests
{
	private static readonly DateTime Now = new(2026, 10, 12, 16, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void ToUsageLimits_TurnsPremiumRequestsIntoAMonthlyWindowTheGuardHolds()
	{
		var reset = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
		var limits = CopilotQuotaReader.ToUsageLimits(new Dictionary<string, AccountQuotaSnapshot>
		{
			["chat"] = new() { IsUnlimitedEntitlement = true, EntitlementRequests = -1 },
			[CopilotQuotaReader.PremiumQuotaKey] = new()
			{
				EntitlementRequests = 300,
				UsedRequests = 276,
				RemainingPercentage = 8,
				ResetDate = reset
			}
		}, Now);

		var window = Assert.Single(limits!.Windows);
		Assert.Equal(UsageLimitWindowScope.Monthly, window.Scope);
		Assert.Equal(92, window.PercentUsed);
		Assert.Equal(reset.UtcDateTime, window.ResetTime);
		Assert.False(window.IsLimitReached);

		var hold = SessionLimitPolicy.Evaluate(limits.Windows, 90, null, Now, 90);
		Assert.Equal(reset.UtcDateTime, hold?.Until);
	}

	[Fact]
	public void ToUsageLimits_WaitsForTheNextMonth_WhenTheReportedResetHasPassed()
	{
		var limits = CopilotQuotaReader.ToUsageLimits(new Dictionary<string, AccountQuotaSnapshot>
		{
			[CopilotQuotaReader.PremiumQuotaKey] = new()
			{
				EntitlementRequests = 1500,
				UsedRequests = 1400,
				RemainingPercentage = 6,
				ResetDate = new DateTimeOffset(Now.AddHours(-1))
			}
		}, Now);

		var expectedReset = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
		Assert.Equal(expectedReset, limits!.Windows[0].ResetTime);
		Assert.Equal(expectedReset, SessionLimitPolicy.Evaluate(limits.Windows, 90, null, Now, 90)?.Until);
	}

	[Fact]
	public void ToUsageLimits_MarksAnExhaustedQuota_AndMentionsOverage()
	{
		var limits = CopilotQuotaReader.ToUsageLimits(new Dictionary<string, AccountQuotaSnapshot>
		{
			[CopilotQuotaReader.PremiumQuotaKey] = new()
			{
				EntitlementRequests = 300,
				UsedRequests = 300,
				RemainingPercentage = 0,
				OverageAllowedWithExhaustedQuota = true
			}
		}, Now);

		Assert.True(limits!.IsLimitReached);
		Assert.Contains("overage", limits.Message);
	}

	[Fact]
	public void ToUsageLimits_ReturnsNothingForAnUnlimitedPlanOrAMissingQuota()
	{
		Assert.Null(CopilotQuotaReader.ToUsageLimits(new Dictionary<string, AccountQuotaSnapshot>
		{
			[CopilotQuotaReader.PremiumQuotaKey] = new() { IsUnlimitedEntitlement = true, EntitlementRequests = -1 }
		}, Now));
		Assert.Null(CopilotQuotaReader.ToUsageLimits(new Dictionary<string, AccountQuotaSnapshot>(), Now));
		Assert.Null(CopilotQuotaReader.ToUsageLimits(null, Now));
	}

	[Fact]
	public void NeedsLiveReading_ReadsAFreeQuotaBeforeTheFirstJob_ButNotTwiceInAMoment()
	{
		var now = DateTime.UtcNow;

		Assert.True(ProviderSessionLimitGuard.NeedsLiveReading(null, null, now, readingIsFree: true));
		Assert.False(ProviderSessionLimitGuard.NeedsLiveReading(null, null, now));
		Assert.False(ProviderSessionLimitGuard.NeedsLiveReading(
			new ProviderUsageSummary { LimitsRefreshedAt = now.AddSeconds(-30) }, null, now, readingIsFree: true));
		Assert.True(ProviderSessionLimitGuard.NeedsLiveReading(
			new ProviderUsageSummary { LimitsRefreshedAt = now.AddMinutes(-10) }, null, now, readingIsFree: true));
	}
}
#pragma warning restore GHCP001
