using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class BuildVerificationPromptTests
{
	[Fact]
	public void BuildSystemPromptRules_IncludesBuildVerificationSection()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.Contains("BUILD VERIFICATION (CRITICAL):", rules);
		Assert.Contains("MUST verify", rules);
		Assert.Contains("Never leave the project in a broken state", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_IncludesConfiguredBuildCommand()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "DotNet Project",
			WorkingPath = "/tmp/test",
			BuildCommand = "dotnet build",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.Contains("dotnet build", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_IncludesConfiguredTestCommand()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "DotNet Project",
			WorkingPath = "/tmp/test",
			TestCommand = "dotnet test",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.Contains("dotnet test", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_FallsBackToGenericWhenNoBuildCommandConfigured()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Generic Project",
			WorkingPath = "/tmp/test",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.Contains("appropriate build command", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_OmitsBuildVerificationWhenEfficiencyRulesDisabled()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			BuildCommand = "dotnet build",
			Environments = []
		}, injectEfficiencyRules: false);

		// When efficiency rules are disabled, build verification section should also be absent
		Assert.True(rules == null || !rules.Contains("BUILD VERIFICATION"));
	}

	[Fact]
	public void BuildSystemPromptRules_IncludesProviderCommitAttributionGuidance_WhenEnabled()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(
			new Project
			{
				Name = "Test Project",
				WorkingPath = "/tmp/test",
				Environments = []
			},
			providerType: ProviderType.Copilot,
			enableCommitAttribution: true);

		Assert.NotNull(rules);
		Assert.Contains("COMMIT ATTRIBUTION:", rules);
		Assert.Contains("Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>", rules);
	}

	[Fact]
	public void BuildExecutionPrompt_AppendsApprovedImplementationPlan()
	{
		var prompt = PromptBuilder.BuildExecutionPrompt(new Job
		{
			GoalPrompt = "Implement the feature",
			Project = new Project
			{
				Name = "Prompt Project",
				WorkingPath = "/tmp/test",
				Environments = []
			}
		}, "1. Inspect the codebase\n2. Implement the change");

		Assert.Contains("<implementation_plan>", prompt);
		Assert.Contains("Use the implementation plan above as the approved plan for this task.", prompt);
		Assert.Contains("Execute the work now.", prompt);
	}

	[Fact]
	public void BuildSystemPromptRules_IncludesDisableAttributionGuidance_WhenDisabled()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(
			new Project
			{
				Name = "Test Project",
				WorkingPath = "/tmp/test",
				Environments = []
			},
			providerType: ProviderType.Claude,
			enableCommitAttribution: false);

		Assert.NotNull(rules);
		Assert.Contains("do not add provider attribution", rules);
		Assert.Contains("repository's existing git identity", rules);
	}

	[Fact]
	public void BuildIdeaImplementationPrompt_UsesConfiguredTemplate()
	{
		var prompt = PromptBuilder.BuildIdeaImplementationPrompt(
			"Add bulk archive controls",
			"""
			Explore first.

			Idea:
			{{idea}}
			""");

		Assert.Contains("Explore first.", prompt);
		Assert.Contains("Add bulk archive controls", prompt);
		Assert.DoesNotContain("Work directly from the idea below", prompt);
	}

	[Fact]
	public void BuildApprovedIdeaImplementationPrompt_AppendsMissingSectionsWhenTemplateOmitsTokens()
	{
		var prompt = PromptBuilder.BuildApprovedIdeaImplementationPrompt(
			"Add archive controls",
			"Implement a bulk action bar.",
			"Keep this concise.");

		Assert.Contains("Keep this concise.", prompt);
		Assert.Contains("## Original Idea", prompt);
		Assert.Contains("Add archive controls", prompt);
		Assert.Contains("## Detailed Specification", prompt);
		Assert.Contains("Implement a bulk action bar.", prompt);
	}
}
