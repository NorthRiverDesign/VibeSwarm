using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProviderPlanningHelperTests
{
	[Fact]
	public void ExtractExecutionText_PrefersPlanMessages()
	{
		var result = new ExecutionResult
		{
			Output = "fallback output",
			Messages =
			[
				new ExecutionMessage { Role = "assistant", Content = "assistant reply" },
				new ExecutionMessage { Role = "plan", Content = "step 1\nstep 2" }
			]
		};

		var extracted = ProviderPlanningHelper.ExtractExecutionText(result);

		Assert.Equal("step 1\nstep 2", extracted);
	}

	[Fact]
	public void BuildPlanningPrompt_UsesSlashCommandForClaude()
	{
		var prompt = ProviderPlanningHelper.BuildPlanningPrompt(ProviderType.Claude, "Implement a dashboard");

		Assert.StartsWith("/plan ", prompt, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildPlanningPrompt_DoesNotUseSlashCommandForCopilot()
	{
		var prompt = ProviderPlanningHelper.BuildPlanningPrompt(ProviderType.Copilot, "Implement a dashboard");

		Assert.DoesNotContain("/plan", prompt, StringComparison.Ordinal);
		Assert.StartsWith("Create an implementation-ready plan", prompt, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProviderType.Claude, true)]
	[InlineData(ProviderType.Copilot, true)]
	[InlineData(ProviderType.OpenCode, false)]
	public void SupportsPlanningMode_ReturnsExpectedValue(ProviderType providerType, bool expected)
	{
		Assert.Equal(expected, ProviderPlanningHelper.SupportsPlanningMode(providerType));
	}
}
