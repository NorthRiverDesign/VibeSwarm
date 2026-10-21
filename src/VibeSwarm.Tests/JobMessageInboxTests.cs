using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobMessageInboxTests
{
	[Fact]
	public void TryPost_RefusesMessagesUntilOpenedAndOnceClosed()
	{
		var inbox = new JobMessageInbox();
		Assert.False(inbox.TryPost("Before the agent starts"));

		inbox.Open();
		Assert.True(inbox.TryPost("While it works"));

		inbox.Close();
		Assert.False(inbox.TryPost("After it finished"));
		Assert.False(inbox.HasPending);
	}

	[Fact]
	public void TryPost_StopsTheTurnInProgress_AndKeepsTheMessageForTheNext()
	{
		var inbox = new JobMessageInbox();
		inbox.Open();
		using var turn = new CancellationTokenSource();
		Assert.Empty(inbox.BeginTurn(turn));

		Assert.True(inbox.TryPost("Use tabs"));

		Assert.True(turn.IsCancellationRequested);
		Assert.Equal(["Use tabs"], inbox.Take());
		Assert.False(inbox.HasPending);
	}

	[Fact]
	public void BeginTurn_TakesMessagesSentBetweenTurns_AndAnEndedTurnIsNotStopped()
	{
		var inbox = new JobMessageInbox();
		inbox.Open();
		using var firstTurn = new CancellationTokenSource();
		inbox.BeginTurn(firstTurn);
		inbox.EndTurn(firstTurn);

		Assert.True(inbox.TryPost("First"));
		Assert.True(inbox.TryPost("Second"));
		Assert.False(firstTurn.IsCancellationRequested);

		using var nextTurn = new CancellationTokenSource();
		Assert.Equal(["First", "Second"], inbox.BeginTurn(nextTurn));
		Assert.False(nextTurn.IsCancellationRequested);
	}

	[Fact]
	public void TakeOrClose_KeepsTheInboxOpenWhileMessagesWait_AndClosesItOtherwise()
	{
		var inbox = new JobMessageInbox();
		inbox.Open();
		inbox.TryPost("One more thing");

		Assert.Equal(["One more thing"], inbox.TakeOrClose());
		Assert.True(inbox.TryPost("And another"));

		inbox.Take();
		Assert.Empty(inbox.TakeOrClose());
		Assert.False(inbox.TryPost("Too late"));
	}

	[Fact]
	public void TryPost_IgnoresATurnWhoseCancellationSourceIsAlreadyDisposed()
	{
		var inbox = new JobMessageInbox();
		inbox.Open();
		var turn = new CancellationTokenSource();
		inbox.BeginTurn(turn);
		turn.Dispose();

		Assert.True(inbox.TryPost("Still delivered next turn"));
		Assert.True(inbox.HasPending);
	}

	[Fact]
	public void BuildUserMessagesPrompt_SaysWhenTheTurnWasStopped()
	{
		var interrupted = PromptBuilder.BuildUserMessagesPrompt(["Use tabs", "Skip the docs"], interruptedTurn: true);
		Assert.StartsWith("<user_messages>", interrupted);
		Assert.EndsWith("</user_messages>", interrupted);
		Assert.Contains("sent the messages below", interrupted);
		Assert.Contains("Your last step was stopped", interrupted);
		Assert.Contains("Use tabs", interrupted);
		Assert.Contains("Skip the docs", interrupted);

		var between = PromptBuilder.BuildUserMessagesPrompt(["Use tabs"], interruptedTurn: false);
		Assert.Contains("sent the message below", between);
		Assert.DoesNotContain("stopped", between);
	}

	[Fact]
	public void BuildUserMessagesFreshSessionPrompt_CarriesTheTaskTheOutputAndTheMessages()
	{
		var prompt = PromptBuilder.BuildUserMessagesFreshSessionPrompt("Add a settings page", ["Use tabs"], "Edited Settings.razor");

		Assert.StartsWith("Add a settings page", prompt);
		Assert.Contains("<recent_console_output>\nEdited Settings.razor\n</recent_console_output>", prompt.ReplaceLineEndings("\n"));
		Assert.EndsWith("</user_messages>", prompt);
		Assert.Contains("Use tabs", prompt);
	}
}
