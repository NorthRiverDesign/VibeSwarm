using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectTeamPromptTests
{
	[Fact]
	public void BuildStructuredPrompt_IncludesAssignedTeamRoles()
	{
		var bootstrapSkillId = Guid.NewGuid();
		var job = new Job
		{
			GoalPrompt = "Harden auth and improve deployment flow.",
			Project = new Project
			{
				Name = "VibeSwarm",
				WorkingPath = "/tmp/vibeswarm",
				TeamAssignments =
				[
					new ProjectTeamRole
					{
						TeamRoleId = Guid.NewGuid(),
						ProviderId = Guid.NewGuid(),
						PreferredModelId = "gpt-5.4",
						Provider = new Provider
						{
							Id = Guid.NewGuid(),
							Name = "GitHub Copilot"
						},
						TeamRole = new TeamRole
						{
							Id = Guid.NewGuid(),
							Name = "Security Reviewer",
							Description = "Reviews auth and secrets.",
							Responsibilities = "Check auth flows and credential handling.",
							DefaultCycleMode = CycleMode.Autonomous,
							DefaultCycleSessionMode = CycleSessionMode.ContinueSession,
							DefaultMaxCycles = 4,
							SkillLinks =
							[
								new TeamRoleSkill
								{
									SkillId = Guid.NewGuid(),
									Skill = new Skill
									{
										Id = bootstrapSkillId,
										Name = "secure-review",
										Description = "Review auth, secrets, and security-sensitive changes.",
										Content = "Review code for security issues."
									}
								}
							]
						}
					}
				],
				Environments = []
			}
		};

		var prompt = PromptBuilder.BuildStructuredPrompt(job);

		Assert.Contains("<agents>", prompt);
		Assert.Contains("Security Reviewer", prompt);
		Assert.Contains("GitHub Copilot", prompt);
		Assert.Contains("gpt-5.4", prompt);
		Assert.Contains("secure-review", prompt);
		Assert.Contains("autonomous (max 4 cycles)", prompt);
		Assert.Contains("<available_skills>", prompt);
		Assert.Contains("Use them when relevant instead of guessing project conventions", prompt);
		Assert.Contains("Review auth, secrets, and security-sensitive changes.", prompt);
	}

	[Fact]
	public void BuildRoleSystemPromptContext_IncludesAssignedSkillSummaries()
	{
		var context = PromptBuilder.BuildRoleSystemPromptContext(
			new TeamRole
			{
				Id = Guid.NewGuid(),
				Name = "UI Reviewer",
				Description = "Catches UI polish issues.",
				Responsibilities = "Review Bootstrap usage and responsive layout details.",
				SkillLinks =
				[
					new TeamRoleSkill
					{
						SkillId = Guid.NewGuid(),
						Skill = new Skill
						{
							Id = Guid.NewGuid(),
							Name = "bootstrap-ui",
							Description = "Prefer Bootstrap utilities and avoid custom CSS unless needed.",
							Content = "Bootstrap skill"
						}
					}
				]
			},
			totalSwarmSize: 2);

		Assert.Contains("Agent purpose: Catches UI polish issues.", context);
		Assert.Contains("Available MCP skills for this agent:", context);
		Assert.Contains("bootstrap-ui - Prefer Bootstrap utilities and avoid custom CSS unless needed.", context);
		Assert.Contains("Use those skills when they match the task instead of guessing project-specific conventions.", context);
	}
}
