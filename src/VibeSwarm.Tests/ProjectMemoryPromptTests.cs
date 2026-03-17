using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Tests;

public sealed class ProjectMemoryPromptTests
{
	[Fact]
	public void BuildProjectMemoryRules_IncludesFilePathAndPersistenceInstructions()
	{
		var rules = PromptBuilder.BuildProjectMemoryRules(new Project
		{
			Name = "Docs App",
			WorkingPath = "/tmp/docs-app",
			Memory = "Remember to run migrations before deploys."
		}, "/tmp/docs-app/.vibeswarm/project-memory.md");

		Assert.NotNull(rules);
		Assert.Contains("PROJECT MEMORY:", rules);
		Assert.Contains("/tmp/docs-app/.vibeswarm/project-memory.md", rules);
		Assert.Contains("update this file", rules, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(ValidationLimits.ProjectMemoryMaxLength.ToString(), rules, StringComparison.Ordinal);
		Assert.Contains("sync changes", rules, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void BuildProjectMemoryRules_IncludesBootstrapGuidanceWhenMemoryIsEmpty()
	{
		var rules = PromptBuilder.BuildProjectMemoryRules(new Project
		{
			Name = "Fresh Project",
			WorkingPath = "/tmp/fresh-project"
		}, "/tmp/fresh-project/.vibeswarm/project-memory.md");

		Assert.NotNull(rules);
		Assert.Contains("If the file is empty", rules);
	}
}
