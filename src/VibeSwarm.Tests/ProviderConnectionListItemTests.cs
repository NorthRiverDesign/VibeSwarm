using Bunit;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class ProviderConnectionListItemTests
{
	[Fact]
	public void ProviderConnectionListItem_ShowsLabeledMoreActionsButton()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderConnectionListItem>(parameters => parameters
			.Add(component => component.Provider, CreateProvider()));

		Assert.Contains(">More<", cut.Markup);
	}

	[Fact]
	public void ProviderConnectionListItem_TogglesExpandedModelListWithMultipliers()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderConnectionListItem>(parameters => parameters
			.Add(component => component.Provider, CreateProvider())
			.Add(component => component.Models,
			[
				new ProviderModel
				{
					Id = Guid.NewGuid(),
					ProviderId = Guid.NewGuid(),
					ModelId = "claude-3.7-sonnet",
					DisplayName = "Claude Sonnet 3.7",
					IsAvailable = true,
					PriceMultiplier = 1.5m
				},
				new ProviderModel
				{
					Id = Guid.NewGuid(),
					ProviderId = Guid.NewGuid(),
					ModelId = "claude-haiku",
					DisplayName = "Claude Haiku",
					IsDefault = true,
					IsAvailable = false,
					PriceMultiplier = 0.5m
				}
			]));

		Assert.DoesNotContain("Claude Sonnet 3.7", cut.Markup);
		Assert.Contains("1 model", cut.Markup);

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("1 model", StringComparison.Ordinal))
			.Click();

		Assert.Contains("Claude Sonnet 3.7", cut.Markup);
		Assert.Contains("1.5x", cut.Markup);
		Assert.DoesNotContain("claude-haiku", cut.Markup);
		Assert.DoesNotContain("0.5x", cut.Markup);
		Assert.Contains("Default model unavailable.", cut.Markup);
	}


	[Fact]
	public void UsagePanel_OffersTheCheckButtonAndExplainsItselfBeforeAnyDataExists()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderConnectionListItem>(parameters => parameters
			.Add(component => component.Provider, CreateProvider())
			.Add(component => component.SupportsUsageRefresh, true));

		Assert.Contains("Check usage", cut.Markup);
		Assert.Contains("No usage recorded yet", cut.Markup);
	}

	[Fact]
	public void UsagePanel_IsHiddenWhenTheProviderCannotReportUsage()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderConnectionListItem>(parameters => parameters
			.Add(component => component.Provider, CreateProvider())
			.Add(component => component.SupportsUsageRefresh, false));

		Assert.DoesNotContain("Check usage", cut.Markup);
	}

	[Fact]
	public void UsagePanel_RendersOneMeterPerReportedWindow()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderConnectionListItem>(parameters => parameters
			.Add(component => component.Provider, CreateProvider())
			.Add(component => component.SupportsUsageRefresh, true)
			.Add(component => component.UsageSummary, new ProviderUsageSummary
			{
				ProviderId = Guid.NewGuid(),
				LimitType = UsageLimitType.RateLimit,
				LimitWindows =
				[
					new UsageLimitWindow
					{
						Label = "Session (5 hours)",
						Scope = UsageLimitWindowScope.Session,
						LimitType = UsageLimitType.SessionLimit,
						CurrentUsage = 10,
						MaxUsage = 100
					},
					new UsageLimitWindow
					{
						Label = "Weekly",
						Scope = UsageLimitWindowScope.Weekly,
						LimitType = UsageLimitType.RateLimit,
						CurrentUsage = 34,
						MaxUsage = 100
					},
					new UsageLimitWindow
					{
						Label = "Weekly (with overage)",
						Scope = UsageLimitWindowScope.Weekly,
						LimitType = UsageLimitType.RateLimit,
						CurrentUsage = 0,
						MaxUsage = 100
					}
				]
			}));

		Assert.Equal(3, cut.FindAll(".progress-bar").Count);
		Assert.Contains("Session (5 hours)", cut.Markup);
		Assert.Contains("Weekly (with overage)", cut.Markup);
		Assert.Contains("10%", cut.Markup);
		Assert.Contains("34%", cut.Markup);
		// The window carries its own name and the percentage sits beside it, so the
		// raw "10 / 100" pair is not repeated.
		Assert.DoesNotContain("10 / 100", cut.Markup);
	}

	[Fact]
	public void CheckUsageButton_RaisesTheRefreshCallbackForThisProvider()
	{
		using var context = new BunitContext();
		var provider = CreateProvider();
		Provider? refreshed = null;

		var cut = context.Render<ProviderConnectionListItem>(parameters => parameters
			.Add(component => component.Provider, provider)
			.Add(component => component.SupportsUsageRefresh, true)
			.Add(component => component.OnRefreshUsage, p => refreshed = p));

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Check usage", StringComparison.Ordinal))
			.Click();

		Assert.Same(provider, refreshed);
	}

	[Fact]
	public void UsagePanel_ShowsWhyARefreshFailed()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderConnectionListItem>(parameters => parameters
			.Add(component => component.Provider, CreateProvider())
			.Add(component => component.SupportsUsageRefresh, true)
			.Add(component => component.UsageRefreshError, "Timed out waiting for the provider to report usage."));

		Assert.Contains("Timed out waiting for the provider to report usage.", cut.Markup);
	}

	[Fact]
	public void ProviderConnectionListItem_ShowsWarningWhenDefaultModelIsUnavailable()
	{
		using var context = new BunitContext();

		var cut = context.Render<ProviderConnectionListItem>(parameters => parameters
			.Add(component => component.Provider, CreateProvider())
			.Add(component => component.Models,
			[
				new ProviderModel
				{
					Id = Guid.NewGuid(),
					ProviderId = Guid.NewGuid(),
					ModelId = "claude-sonnet-4.6",
					DisplayName = "Claude Sonnet 4.6",
					IsAvailable = true
				},
				new ProviderModel
				{
					Id = Guid.NewGuid(),
					ProviderId = Guid.NewGuid(),
					ModelId = "claude-haiku",
					DisplayName = "Claude Haiku",
					IsDefault = true,
					IsAvailable = false
				}
			]));

		Assert.Contains("Default model unavailable.", cut.Markup);
		Assert.Contains("Claude Haiku is no longer available.", cut.Markup);
	}

	private static Provider CreateProvider()
	{
		return new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude CLI Main",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
	}
}
