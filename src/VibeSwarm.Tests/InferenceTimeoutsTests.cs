using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class InferenceTimeoutsTests
{
	[Fact]
	public void IdeaActionTimeout_UsesLongerLimitForInference()
	{
		Assert.Equal(TimeSpan.FromMinutes(5), InferenceTimeouts.GetIdeaActionTimeout(useInference: false));
		Assert.Equal(TimeSpan.FromMinutes(60), InferenceTimeouts.GetIdeaActionTimeout(useInference: true));
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
	}
}
