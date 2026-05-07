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
		Assert.Contains("Verify the project builds before finishing.", rules);
		Assert.Contains("Do not leave the repository in a broken state.", rules);
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
		Assert.Contains("Treat the implementation plan above as approved.", prompt);
		Assert.Contains("Implement it now.", prompt);
	}

	[Fact]
	public void BuildRecoveryPrompt_IncludesFreshSessionGuidanceAndRecentOutput()
	{
		var prompt = PromptBuilder.BuildRecoveryPrompt(
			"Implement the feature",
			"Resume the interrupted job",
			"[ERR] try again in 1 minute",
			"Stored session no longer exists",
			forceFreshSession: true);

		Assert.Contains("Resume the interrupted job", prompt);
		Assert.Contains("fresh session", prompt);
		Assert.Contains("Stored session no longer exists", prompt);
		Assert.Contains("<recent_console_output>", prompt);
		Assert.Contains("try again in 1 minute", prompt);
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
	public void BuildIdeaSystemPromptRules_OmitsCommitAttributionSection()
	{
		var rules = PromptBuilder.BuildIdeaSystemPromptRules(new Project
		{
			Name = "Idea Project",
			WorkingPath = "/tmp/test",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.Contains("BUILD VERIFICATION (CRITICAL):", rules);
		Assert.DoesNotContain("COMMIT ATTRIBUTION:", rules);
		Assert.DoesNotContain("Co-authored-by: Copilot", rules);
		Assert.DoesNotContain("provider-specific trailers", rules);
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
	public void BuildIdeaExpansionPrompt_DefaultTemplate_UsesStaffLevelExplorationGuidance()
	{
		var prompt = PromptBuilder.BuildIdeaExpansionPrompt("Add bulk archive controls");

		Assert.Contains("staff-level software engineer", prompt);
		Assert.Contains("Inspect the codebase, related flows, reusable components, and tests first. Use subagents when they help.", prompt);
		Assert.Contains("No code samples or provider/model attribution.", prompt);
	}

	[Fact]
	public void BuildIdeaImplementationPrompt_DefaultTemplate_UsesStaffLevelExplorationGuidance()
	{
		var prompt = PromptBuilder.BuildIdeaImplementationPrompt("Add bulk archive controls");

		Assert.Contains("staff-level software engineer", prompt);
		Assert.Contains("Inspect the codebase, related flows, reusable components, and tests before editing. Use subagents when they help.", prompt);
		Assert.Contains("Implement the feature end-to-end with the needed UX, validation, persistence, error handling, and tests.", prompt);
		Assert.Contains("leave the repository in a working state", prompt);
		Assert.DoesNotContain("inspect -> plan -> implement -> verify loop", prompt);
		Assert.DoesNotContain("autonomous CI coding job", prompt);
		Assert.Contains("Do not mention or attribute the work to any provider, model, or CLI tool.", prompt);
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

	[Fact]
	public void BuildApprovedIdeaImplementationPrompt_DefaultTemplate_AvoidsProviderAttribution()
	{
		var prompt = PromptBuilder.BuildApprovedIdeaImplementationPrompt(
			"Add archive controls",
			"Implement a bulk action bar.");

		Assert.Contains("staff-level software engineer", prompt);
		Assert.Contains("Use the approved specification as the source of truth", prompt);
		Assert.Contains("Inspect the codebase, related flows, reusable components, and tests before editing. Use subagents when they help.", prompt);
		Assert.Contains("leave the repository in a working state", prompt);
		Assert.DoesNotContain("inspect -> plan -> implement -> verify loop", prompt);
		Assert.DoesNotContain("autonomous CI coding job", prompt);
		Assert.Contains("Do not mention or attribute the work to any provider, model, or CLI tool.", prompt);
	}
}
