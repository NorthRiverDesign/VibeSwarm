using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class ProviderCapabilitiesTests
{
	[Fact]
	public void ClaudeCli_SupportedReasoningEfforts_IncludeMax()
	{
		var efforts = ProviderCapabilities.GetSupportedReasoningEfforts(ProviderType.Claude, ProviderConnectionMode.CLI);

		Assert.Contains("max", efforts);
	}

	[Fact]
	public void ClaudeCli_SupportsReasoningEffort_AcceptsMax()
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI
		};

		Assert.True(ProviderCapabilities.SupportsReasoningEffort(provider, "max"));
	}

	[Theory]
	[InlineData(ProviderConnectionMode.CLI)]
	[InlineData(ProviderConnectionMode.SDK)]
	public void Copilot_SupportedReasoningEfforts_IncludeXHigh(ProviderConnectionMode mode)
	{
		var efforts = ProviderCapabilities.GetSupportedReasoningEfforts(ProviderType.Copilot, mode);

		Assert.Contains("xhigh", efforts);
	}

	[Theory]
	[InlineData(ProviderConnectionMode.CLI)]
	[InlineData(ProviderConnectionMode.SDK)]
	public void Copilot_SupportsReasoningEffort_AcceptsXHigh(ProviderConnectionMode mode)
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = mode
		};

		Assert.True(ProviderCapabilities.SupportsReasoningEffort(provider, "xhigh"));
	}
}
