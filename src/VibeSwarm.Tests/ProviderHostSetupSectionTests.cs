using Bunit;
using VibeSwarm.Client.Components.Providers;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class ProviderHostSetupSectionTests
{
	[Fact]
	public void ProviderHostSetupSection_FiltersConnectionsBySelectedModePill()
	{
		using var context = new BunitContext();

		var cliProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude CLI Main",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true
		};
		var sdkProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude SDK Main",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.SDK,
			IsEnabled = true
		};

		var cut = context.Render<ProviderHostSetupSection>(parameters => parameters
			.Add(component => component.Statuses,
			[
				new CommonProviderSetupStatus
				{
					ProviderType = ProviderType.Claude,
					DisplayName = "Anthropic Claude",
					Description = "Claude setup",
					DocumentationUrl = "https://example.com/docs"
				}
			])
			.Add(component => component.Providers, [cliProvider, sdkProvider])
			.Add(component => component.ActiveProviderTab, ProviderType.Claude.ToString()));

		Assert.Contains("Claude CLI Main", cut.Markup);
		Assert.Contains("Claude SDK Main", cut.Markup);
		Assert.Contains("nav nav-pills", cut.Markup);

		cut.FindAll(".nav-pills .nav-link")
			.Single(button => button.TextContent.Contains("SDK", StringComparison.Ordinal))
			.Click();

		Assert.DoesNotContain("Claude CLI Main", cut.Markup);
		Assert.Contains("Claude SDK Main", cut.Markup);
	}
}
