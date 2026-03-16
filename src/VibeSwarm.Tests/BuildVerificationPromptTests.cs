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
}
