using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProjectTeamPromptTests
{
	[Fact]
	public void BuildStructuredPrompt_IncludesAssignedTeamRoles()
	{
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
							SkillLinks =
							[
								new TeamRoleSkill
								{
									SkillId = Guid.NewGuid(),
									Skill = new Skill
									{
										Id = Guid.NewGuid(),
										Name = "secure-review",
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

		Assert.Contains("<team_roles>", prompt);
		Assert.Contains("Security Reviewer", prompt);
		Assert.Contains("GitHub Copilot", prompt);
		Assert.Contains("gpt-5.4", prompt);
		Assert.Contains("secure-review", prompt);
	}
}
