using Bunit;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

/// <summary>
/// Usage figures must follow the newest reading, and a window that has reset since its last
/// reading must stop showing (and counting) what it held before.
/// </summary>
public sealed class UsageReadingFreshnessTests
{
	private static readonly DateTime Now = DateTime.UtcNow;

	[Fact]
	public void Merge_TakesTheLaterReadingOfAWindow()
	{
		var earlier = Limits(Session(31), Weekly(16));
		var later = Limits(Session(41), Weekly(17));

		var merged = UsageLimitWindowHelper.Merge(earlier, later);

		Assert.Equal(41, merged.Windows.Single(w => w.Label == "Session").CurrentUsage);
		Assert.Equal(17, merged.Windows.Single(w => w.Label == "Weekly").CurrentUsage);
		Assert.Equal(41, merged.CurrentUsage);
	}

	[Fact]
	public void Merge_KeepsWindowsOnlyTheEarlierReadingCarried()
	{
		var merged = UsageLimitWindowHelper.Merge(Limits(Session(31), Weekly(16)), Limits(Session(41)));

		Assert.Equal(41, merged.Windows.Single(w => w.Label == "Session").CurrentUsage);
		Assert.Equal(16, merged.Windows.Single(w => w.Label == "Weekly").CurrentUsage);
	}

	[Fact]
	public void Merge_LetsALaterReadingClearAWindowThatWasFull()
	{
		var full = Session(100);
		full.IsLimitReached = true;

		var merged = UsageLimitWindowHelper.Merge(Limits(full), Limits(Session(3)));

		Assert.False(merged.Windows.Single(w => w.Label == "Session").IsLimitReached);
	}

	[Fact]
	public void AsOf_EmptiesAWindowThatHasReset()
	{
		var window = Session(42, Now.AddMinutes(-10));
		window.IsLimitReached = true;
		window.Message = "42 of 100 used";

		var current = UsageLimitWindowHelper.AsOf(window, Now);

		Assert.Equal(0, current.CurrentUsage);
		Assert.Equal(100, current.MaxUsage);
		Assert.False(current.IsLimitReached);
		Assert.Null(current.Message);
		Assert.Equal(window.ResetTime, current.ResetTime);
	}

	[Fact]
	public void AsOf_LeavesAWindowThatHasNotResetAlone()
	{
		var window = Session(42, Now.AddMinutes(10));

		Assert.Same(window, UsageLimitWindowHelper.AsOf(window, Now));
	}

	[Fact]
	public void IsLimitReachedAt_EndsWhenTheWindowResets()
	{
		var summary = new ProviderUsageSummary
		{
			IsLimitReached = true,
			LimitResetTime = Now.AddMinutes(-1),
			LimitWindows = [Reached(Session(100, Now.AddMinutes(-1)))]
		};

		Assert.False(summary.IsLimitReachedAt(Now));
		Assert.True(summary.IsLimitReachedAt(Now.AddMinutes(-2)));
	}

	[Fact]
	public void IsLimitReachedAt_HoldsWhileALongerReachedWindowHasNotReset()
	{
		var summary = new ProviderUsageSummary
		{
			IsLimitReached = true,
			LimitResetTime = Now.AddMinutes(-1),
			LimitWindows = [Session(100, Now.AddMinutes(-1)), Reached(Weekly(100, Now.AddDays(2)))]
		};

		Assert.True(summary.IsLimitReachedAt(Now));
	}

	[Fact]
	public void UsageMeter_ShowsAWindowThatHasResetAsEmpty()
	{
		using var context = new BunitContext();
		var summary = new ProviderUsageSummary
		{
			LimitType = UsageLimitType.PremiumRequests,
			LimitWindows =
			[
				new UsageLimitWindow
				{
					Scope = UsageLimitWindowScope.Monthly,
					LimitType = UsageLimitType.PremiumRequests,
					Label = "Premium requests",
					CurrentUsage = 5,
					MaxUsage = 1500,
					ResetTime = Now.AddDays(-11),
					Message = "5 of 1,500 premium requests used this month"
				}
			]
		};

		var cut = context.Render<UsageMeter>(parameters => parameters.Add(p => p.UsageSummary, summary));

		Assert.Contains("Premium requests: 0/1500", cut.Markup.Replace(",", string.Empty).Replace(" / ", "/"));
		Assert.Contains("Reset on", cut.Markup);
		Assert.DoesNotContain("5 of 1,500", cut.Markup);
		Assert.DoesNotContain("Resets", cut.Markup);
	}

	[Fact]
	public void UsageMeter_ShowsACurrentWindowAsReported()
	{
		using var context = new BunitContext();
		var summary = new ProviderUsageSummary
		{
			LimitType = UsageLimitType.SessionLimit,
			LimitWindows = [Session(42, Now.AddHours(3))]
		};

		var cut = context.Render<UsageMeter>(parameters => parameters.Add(p => p.UsageSummary, summary));

		Assert.Contains("42%", cut.Markup);
		Assert.Contains("Resets in", cut.Markup);
	}

	private static UsageLimits Limits(params UsageLimitWindow[] windows)
		=> UsageLimitWindowHelper.CreateUsageLimits(UsageLimitType.RateLimit, null, windows);

	private static UsageLimitWindow Session(int percent, DateTime? resetTime = null) => new()
	{
		Scope = UsageLimitWindowScope.Session,
		LimitType = UsageLimitType.SessionLimit,
		Label = "Session",
		CurrentUsage = percent,
		MaxUsage = 100,
		ResetTime = resetTime ?? Now.AddHours(1)
	};

	private static UsageLimitWindow Weekly(int percent, DateTime? resetTime = null) => new()
	{
		Scope = UsageLimitWindowScope.Weekly,
		LimitType = UsageLimitType.RateLimit,
		Label = "Weekly",
		CurrentUsage = percent,
		MaxUsage = 100,
		ResetTime = resetTime ?? Now.AddDays(3)
	};

	private static UsageLimitWindow Reached(UsageLimitWindow window)
	{
		window.IsLimitReached = true;
		return window;
	}
}
