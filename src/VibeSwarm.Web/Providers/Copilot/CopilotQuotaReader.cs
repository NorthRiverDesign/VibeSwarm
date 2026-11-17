using GitHub.Copilot.Rpc;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Web.Providers.Copilot;

/// <summary>
/// Turns the account quota the Copilot runtime reports (<c>account.getQuota</c>) into a monthly
/// usage window, so jobs stop short of the plan's premium requests without the user typing in
/// their allowance. Reading it makes no model request, so it costs nothing.
/// </summary>
#pragma warning disable GHCP001 // The account quota RPC is marked experimental in the SDK
public static class CopilotQuotaReader
{
	/// <summary>The quota that metered agent requests draw on.</summary>
	public const string PremiumQuotaKey = "premium_interactions";

	public static UsageLimits? ToUsageLimits(IDictionary<string, AccountQuotaSnapshot>? snapshots, DateTime utcNow)
	{
		if (snapshots == null
			|| !snapshots.TryGetValue(PremiumQuotaKey, out var premium)
			|| premium == null
			|| premium.IsUnlimitedEntitlement
			|| premium.EntitlementRequests <= 0)
		{
			return null;
		}

		var entitlement = (int)premium.EntitlementRequests;
		var used = (int)Math.Max(premium.UsedRequests, 0);
		// Premium requests reset at the start of each month (UTC). The runtime's reset date has
		// been seen in the past (the time it cached the quota), and a window whose reset has
		// passed holds nothing, so a missing or past date falls back to the next month's start.
		var reportedReset = premium.ResetDate?.UtcDateTime;
		var resetTime = reportedReset is DateTime reset && reset > utcNow
			? reset
			: new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
		var isLimitReached = premium.RemainingPercentage <= 0 || used >= entitlement;
		var message = premium.OverageAllowedWithExhaustedQuota
			? $"{used:N0} of {entitlement:N0} premium requests used this month; more are billed as overage"
			: $"{used:N0} of {entitlement:N0} premium requests used this month";

		var window = new UsageLimitWindow
		{
			LimitType = UsageLimitType.PremiumRequests,
			Scope = UsageLimitWindowScope.Monthly,
			Label = "Premium requests",
			CurrentUsage = used,
			MaxUsage = entitlement,
			ResetTime = resetTime,
			IsLimitReached = isLimitReached,
			Message = message
		};

		return new UsageLimits
		{
			LimitType = UsageLimitType.PremiumRequests,
			CurrentUsage = used,
			MaxUsage = entitlement,
			ResetTime = resetTime,
			IsLimitReached = isLimitReached,
			Message = message,
			Windows = [window]
		};
	}
}
#pragma warning restore GHCP001
