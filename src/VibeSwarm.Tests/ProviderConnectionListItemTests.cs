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
		Assert.Contains("Show Models (1)", cut.Markup);

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Show Models", StringComparison.Ordinal))
			.Click();

		Assert.Contains("Hide Models (1)", cut.Markup);
		Assert.Contains("Claude Sonnet 3.7", cut.Markup);
		Assert.Contains("1.5x", cut.Markup);
		Assert.DoesNotContain("claude-haiku", cut.Markup);
		Assert.DoesNotContain("0.5x", cut.Markup);
		Assert.Contains("Default model unavailable.", cut.Markup);
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
