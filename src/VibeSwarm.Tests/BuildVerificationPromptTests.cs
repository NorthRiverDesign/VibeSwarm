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
		Assert.Contains("BUILD VERIFICATION:", rules);
		Assert.Contains("Before finishing, run", rules);
		Assert.Contains("The next queued job starts from this checkout.", rules);
		Assert.DoesNotContain("CRITICAL", rules);
		Assert.DoesNotContain("IMPORTANT", rules);
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
		Assert.Contains("screenshots, browser traces, logs and scratch files", rules);
		Assert.Contains(".git/info/exclude, not .gitignore", rules);
		Assert.Contains("Keep plans, todo lists and session notes out of the repository", rules);
		Assert.Contains("do not add to agent instruction or memory files", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_KeepsBuildVerificationWhenEfficiencyRulesDisabled()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			BuildCommand = "dotnet build",
			Environments = []
		}, injectEfficiencyRules: false);

		// The setting limits scope only; the agent still has to leave the build green.
		Assert.NotNull(rules);
		Assert.Contains("BUILD VERIFICATION", rules);
		Assert.DoesNotContain("Do only the requested work", rules);
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
		Assert.Contains("never stop to ask a question or wait for confirmation", rules);
		Assert.Contains("do not commit, push, stash, reset, rebase, or switch branches", rules);
		Assert.Contains("The next queued job starts from this checkout.", rules);
		Assert.Contains("End with a short summary", rules);
		Assert.Contains("<commit-summary>", rules);
	}

	[Fact]
	public void BuildSystemPromptRules_KeepsJobCompletionRules_WhenEfficiencyRulesDisabled()
	{
		var rules = PromptBuilder.BuildSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			Environments = []
		}, injectEfficiencyRules: false);

		// Unattended runs depend on these: no questions, no git, and the commit summary.
		Assert.NotNull(rules);
		Assert.Contains("COMPLETING THE JOB:", rules);
		Assert.Contains("<commit-summary>", rules);
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
		Assert.Contains("Before finishing, run `dotnet build`, then `dotnet test`, and fix any failures.", rules);
		Assert.Null(JobSummaryGenerator.ExtractCommitSummary(rules));
		Assert.True(rules.Length <= 1700, $"System prompt rules grew to {rules.Length} characters.");
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
		Assert.Contains("Inspect the codebase, related flows, reusable components, and tests before editing. Use subagents only for broad, parallel research.", prompt);
		Assert.Contains("Implement the feature end-to-end with the needed UX, validation, persistence, error handling, and tests.", prompt);
		// The job's system prompt rules carry the build check and the commit summary.
		Assert.DoesNotContain("leave the repository in a working state", prompt);
		Assert.DoesNotContain("<commit-summary>", prompt);
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
		Assert.Contains("Inspect the codebase, related flows, reusable components, and tests before editing. Use subagents only for broad, parallel research.", prompt);
		Assert.DoesNotContain("leave the repository in a working state", prompt);
		Assert.DoesNotContain("<commit-summary>", prompt);
		Assert.DoesNotContain("inspect -> plan -> implement -> verify loop", prompt);
		Assert.DoesNotContain("autonomous CI coding job", prompt);
		Assert.Contains("Do not mention or attribute the work to any provider, model, or CLI tool.", prompt);
	}

	[Fact]
	public void BuildPlanningSystemPromptRules_KeepsOnlyWhatHelpsAReadOnlyPlanner()
	{
		var rules = PromptBuilder.BuildPlanningSystemPromptRules(new Project
		{
			Name = "Test Project",
			WorkingPath = "/tmp/test",
			BuildCommand = "dotnet build",
			RepoMap = "src/App.cs",
			Environments = []
		}, "/tmp/test/.vibeswarm/project-memory.md");

		Assert.NotNull(rules);
		Assert.Contains("read-only planning step", rules);
		Assert.Contains("state assumptions in the plan instead of asking", rules);
		Assert.Contains("Read /tmp/test/.vibeswarm/project-memory.md first.", rules);
		Assert.Contains("PROJECT STRUCTURE:", rules);
		Assert.Contains("src/App.cs", rules);
		Assert.DoesNotContain("BUILD VERIFICATION", rules);
		Assert.DoesNotContain("dotnet build", rules);
		Assert.DoesNotContain("The deliverable is a code change.", rules);
		Assert.DoesNotContain("<commit-summary>", rules);
	}

	[Fact]
	public void JoinRuleSections_SkipsEmptySections()
	{
		Assert.Null(PromptBuilder.JoinRuleSections(null, " ", ""));
		Assert.Equal(
			$"ROLE{Environment.NewLine}{Environment.NewLine}RULES",
			PromptBuilder.JoinRuleSections("ROLE\n", null, "RULES"));
	}

	[Fact]
	public void BuildStructuredPrompt_LeavesScopeRulesToTheSystemPrompt()
	{
		var project = new Project
		{
			Name = "Prompt Project",
			WorkingPath = "/tmp/test",
			Environments = []
		};

		var withBranch = PromptBuilder.BuildStructuredPrompt(new Job { GoalPrompt = "Fix it", Branch = "develop", Project = project });
		var withoutConstraints = PromptBuilder.BuildStructuredPrompt(new Job { GoalPrompt = "Fix it", Project = project });

		Assert.Contains("Working branch: develop", withBranch);
		Assert.DoesNotContain("Only modify files directly related to the task.", withBranch);
		Assert.DoesNotContain("Do not refactor unrelated code.", withBranch);
		Assert.DoesNotContain("<constraints>", withoutConstraints);
	}
}
