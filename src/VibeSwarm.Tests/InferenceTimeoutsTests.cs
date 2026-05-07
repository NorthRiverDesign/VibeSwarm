using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class InferenceTimeoutsTests
{
	[Fact]
	public void IdeaActionTimeout_UsesLongerLimitForInference()
	{
		Assert.Equal(TimeSpan.FromMinutes(5), InferenceTimeouts.GetIdeaSuggestionTimeout(useInference: false));
		Assert.Equal(TimeSpan.FromMinutes(60), InferenceTimeouts.GetIdeaSuggestionTimeout(useInference: true));
	}

	[Fact]
	public void IdeaExpansionTimeout_TracksSharedInferenceTimeouts()
	{
		Assert.Equal(InferenceTimeouts.StandardIdeaActionTimeout, IdeaService.GetExpansionTimeout(useInference: false));
		Assert.Equal(InferenceTimeouts.InferenceIdeaActionTimeout, IdeaService.GetExpansionTimeout(useInference: true));
	}

	[Fact]
	public void OllamaRuntimeOptions_DefaultsMatchExtendedLocalInferenceTimeouts()
	{
		var options = new OllamaInferenceService.RuntimeOptions();

		Assert.Equal(InferenceTimeouts.LocalInitialResponseTimeout, options.InitialResponseTimeout);
		Assert.Equal(InferenceTimeouts.LocalStreamInactivityTimeout, options.StreamInactivityTimeout);
		Assert.Equal(InferenceTimeouts.LocalGenerationTimeout, options.GenerationTimeout);
		Assert.Equal(InferenceTimeouts.LocalStallDetectionWindow, options.StallDetectionWindow);
	}

	[Fact]
	public void LocalTimeouts_AreExtendedForSlowHardware()
	{
		// Initial response timeout must be long enough for model loading on a Raspberry Pi.
		Assert.True(InferenceTimeouts.LocalInitialResponseTimeout >= TimeSpan.FromMinutes(60));

		// Generation cap must accommodate long completions on ARM/embedded hardware.
		Assert.True(InferenceTimeouts.LocalGenerationTimeout >= TimeSpan.FromMinutes(120));

		// The stall detection window must be shorter than the general inactivity timeout
		// so mid-generation hangs are caught before the full inactivity window expires.
		Assert.True(InferenceTimeouts.LocalStallDetectionWindow < InferenceTimeouts.LocalStreamInactivityTimeout);
	}
}
