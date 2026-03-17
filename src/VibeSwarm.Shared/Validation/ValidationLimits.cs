namespace VibeSwarm.Shared.Validation;

public static class ValidationLimits
{
	public const int ProjectNameMaxLength = 100;
	public const int ProjectDescriptionMaxLength = 2000;
	public const int ProjectWorkingPathMaxLength = 500;
	public const int ProjectGitHubRepositoryMaxLength = 200;
	public const int ProjectGitHubDescriptionMaxLength = 1000;
	public const int ProjectDefaultTargetBranchMaxLength = 250;
	public const int ProjectPlanningModelIdMaxLength = 200;
	public const int ProjectPromptContextMaxLength = 4000;
	public const int ProjectMemoryMaxLength = 20000;

	public const int IdeaDescriptionMaxLength = 8000;
	public const int IdeaExpandedDescriptionMaxLength = 20000;
	public const int IdeaExpansionErrorMaxLength = 1000;
}
