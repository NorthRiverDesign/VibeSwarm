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

	[Fact]
	public void OpenCode_SupportedReasoningEfforts_IncludeVariantPresets()
	{
		var efforts = ProviderCapabilities.GetSupportedReasoningEfforts(ProviderType.OpenCode, ProviderConnectionMode.CLI);

		Assert.Contains("minimal", efforts);
		Assert.Contains("xhigh", efforts);
		Assert.Contains("max", efforts);
	}

	[Theory]
	[InlineData("minimal")]
	[InlineData("xhigh")]
	public void OpenCode_SupportsReasoningEffort_AcceptsVariantPresets(string effort)
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "OpenCode",
			Type = ProviderType.OpenCode,
			ConnectionMode = ProviderConnectionMode.CLI
		};

		Assert.True(ProviderCapabilities.SupportsReasoningEffort(provider, effort));
	}
}
