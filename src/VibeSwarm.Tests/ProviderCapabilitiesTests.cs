using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class ProviderCapabilitiesTests
{
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
