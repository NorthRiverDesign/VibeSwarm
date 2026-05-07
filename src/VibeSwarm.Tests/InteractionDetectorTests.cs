using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Tests;

public sealed class InteractionDetectorTests
{
	[Fact]
	public void DetectInteraction_IgnoresStandaloneNumberedListWithoutPromptContext()
	{
		var interaction = InteractionDetector.DetectInteraction(
			"[1] src/VibeSwarm.Web/Services/JobProcessingService.Execution.cs",
			[
				"Changed files:",
				"[0] README.md"
			]);

		Assert.Null(interaction);
	}

	[Fact]
	public void DetectInteraction_IgnoresInformationalHeaderWithoutPromptCue()
	{
		var interaction = InteractionDetector.DetectInteraction(
			"Available versions:",
			[
				"Provider update check complete",
				"Found 3 versions"
			]);

		Assert.Null(interaction);
	}

	[Fact]
	public void DetectInteraction_DetectsNumberedChoicesWhenPromptContextIsPresent()
	{
		var interaction = InteractionDetector.DetectInteraction(
			"Choose an option:",
			[
				"[1] Continue with the queued migration",
				"Choose an option:",
				"[0] Cancel"
			]);

		Assert.NotNull(interaction);
		Assert.Equal(InteractionDetector.InteractionType.Choice, interaction.Type);
	}

	[Fact]
	public void DetectInteraction_DetectsExplicitBracketedChoiceActions()
	{
		var interaction = InteractionDetector.DetectInteraction("[Accept]");

		Assert.NotNull(interaction);
		Assert.Equal(InteractionDetector.InteractionType.Choice, interaction.Type);
	}
}
