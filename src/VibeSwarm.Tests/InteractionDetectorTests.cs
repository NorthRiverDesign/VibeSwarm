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

	[Theory]
	[InlineData("[System] Process started (PID: 123). Waiting for CLI to initialize...")]
	[InlineData("[System] Still initializing... (waited 5s).")]
	[InlineData("[System] Still waiting for response... (waited 12s).")]
	[InlineData("[System] Still waiting (15s)... Process is running.")]
	[InlineData("[Connection] Connected to provider stream")]
	[InlineData("[Status] Provider initialized")]
	[InlineData("[Planning] Generating plan...")]
	public void DetectInteraction_IgnoresInternalSystemStatusMarkers(string line)
	{
		var interaction = InteractionDetector.DetectInteraction(line);

		Assert.Null(interaction);
	}

	[Theory]
	[InlineData("Still waiting for response from the model...")]
	[InlineData("Waiting for response to come back")]
	[InlineData("waiting for input from CLI buffer")]
	public void DetectInteraction_IgnoresInformationalWaitingPhrasesWithoutUserTargeting(string line)
	{
		var interaction = InteractionDetector.DetectInteraction(line);

		Assert.Null(interaction);
	}

	[Theory]
	[InlineData("Waiting for user input")]
	[InlineData("waiting for your response")]
	[InlineData("waiting for user reply")]
	public void DetectInteraction_DetectsExplicitlyUserTargetedWaitingPrompts(string line)
	{
		var interaction = InteractionDetector.DetectInteraction(line);

		Assert.NotNull(interaction);
		Assert.Equal(InteractionDetector.InteractionType.TextInput, interaction.Type);
	}
}
