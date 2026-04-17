using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectTeamPromptTests
{
	[Fact]
	public void BuildStructuredPrompt_IncludesAssignedAgents()
	{
		var bootstrapSkillId = Guid.NewGuid();
		var storagePath = Path.Combine(Path.GetTempPath(), "vibeswarm-skills", bootstrapSkillId.ToString("N"));
		var job = new Job
		{
			GoalPrompt = "Harden auth and improve deployment flow.",
			Project = new Project
			{
				Name = "VibeSwarm",
				WorkingPath = "/tmp/vibeswarm",
				AgentAssignments =
				[
					new ProjectAgent
					{
						AgentId = Guid.NewGuid(),
						ProviderId = Guid.NewGuid(),
						PreferredModelId = "gpt-5.4",
						Provider = new Provider
						{
							Id = Guid.NewGuid(),
							Name = "GitHub Copilot"
						},
						Agent = new Agent
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
								new AgentSkill
								{
									SkillId = Guid.NewGuid(),
									Skill = new Skill
									{
										Id = bootstrapSkillId,
										Name = "secure-review",
										Description = "Review auth, secrets, and security-sensitive changes.",
										Content = "Review code for security issues.",
										StoragePath = storagePath,
										AllowedTools = "Bash(git:*) Read"
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
		Assert.Contains("read its SKILL.md", prompt);
		Assert.Contains("Review auth, secrets, and security-sensitive changes.", prompt);
		Assert.Contains($"SKILL.md: {Path.Combine(storagePath, "SKILL.md")}", prompt);
		Assert.Contains("allowed-tools: Bash(git:*) Read", prompt);
	}

	[Fact]
	public void BuildStructuredPrompt_OmitsStorageDetailsForLegacySkillsWithoutPath()
	{
		// Skills predating the install metadata feature have no StoragePath yet — they still
		// need to appear in the system prompt (with just name + description) until the storage
		// service materializes them.
		var job = new Job
		{
			GoalPrompt = "Just do the thing.",
			Project = new Project
			{
				Name = "Legacy",
				WorkingPath = "/tmp/legacy",
				AgentAssignments =
				[
					new ProjectAgent
					{
						AgentId = Guid.NewGuid(),
						Agent = new Agent
						{
							Id = Guid.NewGuid(),
							Name = "Any",
							SkillLinks =
							[
								new AgentSkill
								{
									SkillId = Guid.NewGuid(),
									Skill = new Skill
									{
										Id = Guid.NewGuid(),
										Name = "legacy-skill",
										Description = "Pre-install-metadata skill.",
										Content = "Legacy content"
										// StoragePath + AllowedTools deliberately null
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

		Assert.Contains("legacy-skill", prompt);
		Assert.Contains("Pre-install-metadata skill.", prompt);
		Assert.DoesNotContain("SKILL.md:", prompt);
		Assert.DoesNotContain("allowed-tools:", prompt);
	}

	[Fact]
	public void BuildRoleSystemPromptContext_IncludesAssignedSkillSummaries()
	{
		var storagePath = Path.Combine(Path.GetTempPath(), "vibeswarm-skills", "bootstrap-ui");
		var context = PromptBuilder.BuildRoleSystemPromptContext(
			new Agent
			{
				Id = Guid.NewGuid(),
				Name = "UI Reviewer",
				Description = "Catches UI polish issues.",
				Responsibilities = "Review Bootstrap usage and responsive layout details.",
				SkillLinks =
				[
					new AgentSkill
					{
						SkillId = Guid.NewGuid(),
						Skill = new Skill
						{
							Id = Guid.NewGuid(),
							Name = "bootstrap-ui",
							Description = "Prefer Bootstrap utilities and avoid custom CSS unless needed.",
							Content = "Bootstrap skill",
							StoragePath = storagePath,
							AllowedTools = "Read Bash(npx:*)"
						}
					}
				]
			},
			totalSwarmSize: 2);

		Assert.Contains("Agent purpose: Catches UI polish issues.", context);
		Assert.Contains("Skills installed for this agent:", context);
		Assert.Contains("bootstrap-ui - Prefer Bootstrap utilities and avoid custom CSS unless needed.", context);
		Assert.Contains($"SKILL.md: {Path.Combine(storagePath, "SKILL.md")}", context);
		Assert.Contains("allowed-tools: Read Bash(npx:*)", context);
		Assert.Contains("Read each skill's SKILL.md from the path shown before acting", context);
	}
}
