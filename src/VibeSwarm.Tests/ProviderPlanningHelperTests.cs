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

	[Theory]
	[InlineData(ProviderType.Claude)]
	[InlineData(ProviderType.Copilot)]
	public void BuildPlanningPrompt_DoesNotUseSlashCommands(ProviderType providerType)
	{
		var prompt = ProviderPlanningHelper.BuildPlanningPrompt(providerType, "Implement a dashboard");

		Assert.DoesNotContain("/plan", prompt, StringComparison.Ordinal);
		Assert.StartsWith("Explore the codebase and write an implementation-ready plan", prompt, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProviderType.Claude, true)]
	[InlineData(ProviderType.Copilot, true)]
	[InlineData(ProviderType.OpenCode, false)]
	public void SupportsPlanningMode_ReturnsExpectedValue(ProviderType providerType, bool expected)
	{
		Assert.Equal(expected, ProviderPlanningHelper.SupportsPlanningMode(providerType));
	}

	[Fact]
	public void PlanningDisallowedTools_BlocksWriteAndShellTools()
	{
		var tools = ProviderPlanningHelper.PlanningDisallowedTools;

		Assert.Contains("Bash", tools);
		Assert.Contains("Edit", tools);
		Assert.Contains("Write", tools);
		Assert.Contains("MultiEdit", tools);
		Assert.DoesNotContain("Read", tools);
		Assert.DoesNotContain("Glob", tools);
		Assert.DoesNotContain("Grep", tools);
	}
}
