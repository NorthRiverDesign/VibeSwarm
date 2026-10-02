using Bunit;
using VibeSwarm.Client.Components.Agents;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class AgentRowTests
{
	[Fact]
	public void OpenedRow_ShowsDefaultsDutiesAndLinkedSkills()
	{
		using var context = new BunitContext();
		var skill = new Skill { Id = Guid.NewGuid(), Name = "secure-review" };
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Security Reviewer",
			Description = "Focuses on threats and auth flaws.",
			Responsibilities = "Review auth, secrets, and permission boundaries.",
			DefaultProvider = new Provider { Id = Guid.NewGuid(), Name = "Claude Code" },
			DefaultModelId = "claude-sonnet-4.6",
			DefaultCycleMode = CycleMode.Autonomous,
			DefaultCycleSessionMode = CycleSessionMode.ContinueSession,
			DefaultMaxCycles = 4,
			IsEnabled = true,
			SkillLinks = [new AgentSkill { SkillId = skill.Id, Skill = skill }]
		};

		var cut = context.Render<AgentRow>(parameters => parameters.Add(component => component.Agent, agent));
		cut.Find("button[aria-expanded]").Click();

		Assert.Contains("Claude Code", cut.Markup);
		Assert.Contains("claude-sonnet-4.6", cut.Markup);
		Assert.Contains("Autonomous, up to 4 cycles, resumes the session", cut.Markup);
		Assert.Contains("Review auth, secrets, and permission boundaries.", cut.Markup);
		Assert.Contains("1 linked skill", cut.Markup);
		Assert.Contains("secure-review", cut.Markup);
	}

	[Fact]
	public void DisabledAgent_SaysOffAndDeleteRaisesTheCallback()
	{
		using var context = new BunitContext();
		var agent = new Agent { Id = Guid.NewGuid(), Name = "Docs Writer", IsEnabled = false };
		Agent? deleted = null;

		var cut = context.Render<AgentRow>(parameters => parameters
			.Add(component => component.Agent, agent)
			.Add(component => component.OnDelete, a => deleted = a));

		Assert.Contains(">Off<", cut.Markup);

		cut.Find("button[aria-expanded]").Click();
		cut.FindAll("button").Single(button => button.TextContent.Trim() == "Delete").Click();

		Assert.Same(agent, deleted);
	}
}
