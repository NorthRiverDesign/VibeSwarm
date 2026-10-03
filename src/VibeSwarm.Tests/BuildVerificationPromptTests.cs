using VibeSwarm.Shared.Data;
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
	public void BuildSystemPromptRules_KeepsSessionArtifactsOutOfTheRepository()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Idea Project",
			WorkingPath = "/tmp/test",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.Contains("SESSION ARTIFACTS:", rules);
		Assert.Contains("screenshots, browser traces, test logs and reports", rules);
		Assert.Contains(".git/info/exclude, not .gitignore", rules);
		Assert.Contains("Keep plans, todo lists and session notes out of the repository", rules);
		Assert.Contains("Do not add or append to agent instruction, plan or memory files", rules);
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
	public void BuildSystemPromptRules_OmitsProviderCommitAttribution()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.DoesNotContain("COMMIT ATTRIBUTION:", rules);
		Assert.DoesNotContain("Co-authored-by", rules);
		Assert.DoesNotContain(CommitAttributionHelper.ClaudeEmail, rules);
		Assert.DoesNotContain(CommitAttributionHelper.CopilotEmail, rules);
		Assert.DoesNotContain(CommitAttributionHelper.OpenCodeEmail, rules);
	}

	[Fact]
	public void BuildSystemPromptRules_IncludesUnattendedJobCompletionRules()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.Contains("COMPLETING THE JOB:", rules);
		Assert.Contains("Do not stop to ask questions", rules);
		Assert.Contains("do not commit, push, stash, reset, rebase, or switch branches", rules);
		Assert.Contains("The next queued job starts from it.", rules);
		Assert.Contains("End with a short summary", rules);
		Assert.Contains("<commit-summary>", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_OmitsJobCompletionRules_WhenEfficiencyRulesDisabled()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			Environments = []
		}, injectEfficiencyRules: false);

		Assert.True(rules == null || !rules.Contains("COMPLETING THE JOB:"));
	}

	[Fact]
	public void BuildSystemPromptRules_StaysCompact()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			BuildCommand = "dotnet build",
			TestCommand = "dotnet test",
			Environments = []
		});

		Assert.NotNull(rules);
		Assert.Contains("Run `dotnet build`, then `dotnet test`, and fix any failures.", rules);
		Assert.Null(JobSummaryGenerator.ExtractCommitSummary(rules));
		Assert.True(rules.Length <= 2000, $"System prompt rules grew to {rules.Length} characters.");
	}

	[Fact]
	public void BuildStructuredPrompt_KeepsProjectContextInFull_WhenGoalIsLong()
	{
		var goal = "Implement the idea. " + new string('g', 7900);
		var context = "Always build and redeploy on this machine. " + new string('c', 3900);
		var prompt = PromptBuilder.BuildStructuredPrompt(new Job
		{
			GoalPrompt = goal,
			Branch = "develop",
			Project = new Project
			{
				Name = "Prompt Project",
				Description = "The app itself.",
				WorkingPath = "/tmp/test",
				PromptContext = context,
				Environments = []
			}
		});

		Assert.StartsWith("<task>", prompt);
		Assert.Contains(goal, prompt);
		Assert.Contains($"  {context}{Environment.NewLine}", prompt);
		Assert.Contains("<name>Prompt Project</name>", prompt);
		Assert.Contains("Working branch: develop", prompt);
		Assert.EndsWith("</constraints>", prompt);
	}

	[Fact]
	public void BuildStructuredPrompt_LeavesPullRequestCreationToVibeSwarm()
	{
		var prompt = PromptBuilder.BuildStructuredPrompt(new Job
		{
			GoalPrompt = "Implement the feature",
			GitChangeDeliveryMode = GitChangeDeliveryMode.PullRequest,
			Project = new Project
			{
				Name = "Prompt Project",
				WorkingPath = "/tmp/test",
				Environments = []
			}
		});

		Assert.Contains("VibeSwarm opens a pull request for your changes after you finish.", prompt);
		Assert.Contains("Do not open one yourself.", prompt);
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
	public void BuildInteractionResumePrompt_IncludesPromptResponseAndResumeGuidance()
	{
		var prompt = PromptBuilder.BuildInteractionResumePrompt(
			"Implement the feature",
			"Which migration should I apply?",
			"Apply only the pending job-state migration.",
			"[Assistant] Waiting for clarification");

		Assert.Contains("<interaction_context>", prompt);
		Assert.Contains("<provider_prompt>", prompt);
		Assert.Contains("Which migration should I apply?", prompt);
		Assert.Contains("<user_response>", prompt);
		Assert.Contains("Apply only the pending job-state migration.", prompt);
		Assert.Contains("continue the job normally", prompt);
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
