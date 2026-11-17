using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Settings;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>A provider's row in Settings' Usage limits: its thresholds, and its job limit for admins.</summary>
public sealed class UsageLimitRowTests
{
	[Fact]
	public void NonAdmin_SeesTheJobLimitLocked_AndSavesThresholdsWithoutIt()
	{
		var providers = new RecordingProviderService();
		using var context = CreateContext(providers);
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true, MaxConcurrentJobs = 2 };
		var cut = context.Render<UsageLimitRow>(parameters => parameters
			.Add(p => p.Provider, provider)
			.Add(p => p.CanEditJobLimit, false));

		cut.Find("button").Click();
		Assert.True(cut.Find($"#jobs-{provider.Id}").HasAttribute("disabled"));
		Assert.Contains("Only an admin can change this.", cut.Markup);

		cut.Find($"#session-{provider.Id}").Change("80");

		var saved = Assert.Single(providers.Saved);
		Assert.Equal(80, saved.SessionPausePercent);
		Assert.Null(saved.MaxConcurrentJobs);
	}

	[Fact]
	public void Admin_ChangesHowManyJobsTheProviderRunsAtOnce()
	{
		var providers = new RecordingProviderService();
		using var context = CreateContext(providers);
		var provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, IsEnabled = true };
		var cut = context.Render<UsageLimitRow>(parameters => parameters
			.Add(p => p.Provider, provider)
			.Add(p => p.CanEditJobLimit, true));

		cut.Find("button").Click();
		cut.Find($"#jobs-{provider.Id}").Change("3");

		Assert.Equal(3, Assert.Single(providers.Saved).MaxConcurrentJobs);
		Assert.Equal(3, provider.MaxConcurrentJobs);
		Assert.Contains("3 jobs at once", cut.Markup);
	}

	private static BunitContext CreateContext(RecordingProviderService providers)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProviderService>(providers);
		context.Services.AddSingleton<NotificationService>();
		return context;
	}

	private sealed class RecordingProviderService : FakeProviderServiceBase
	{
		public List<ProviderUsageLimits> Saved { get; } = [];

		public override Task<Provider> UpdateUsageLimitsAsync(Guid id, ProviderUsageLimits limits, CancellationToken cancellationToken = default)
		{
			Saved.Add(limits);
			return Task.FromResult(new Provider
			{
				Id = id,
				SessionLimitPauseThresholdPercent = limits.SessionPausePercent,
				WeeklyLimitPauseThresholdPercent = limits.WeeklyPausePercent,
				ConfiguredUsageLimit = limits.ConfiguredUsageLimit,
				MaxConcurrentJobs = limits.MaxConcurrentJobs ?? 2
			});
		}
	}
}
