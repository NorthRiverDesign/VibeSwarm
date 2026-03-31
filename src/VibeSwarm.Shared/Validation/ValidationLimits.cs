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
	public const int ReasoningEffortMaxLength = 32;
	public const int ProjectPromptContextMaxLength = 4000;
	public const int ProjectMemoryMaxLength = 20000;
	public const int JobSchedulePromptMaxLength = 2000;
	public const int JobScheduleModelIdMaxLength = 200;
	public const int JobScheduleLastErrorMaxLength = 1000;
	public const int TeamRoleDescriptionMaxLength = 500;
	public const int TeamRoleResponsibilitiesMaxLength = 4000;

	public const int IdeaDescriptionMaxLength = 8000;
	public const int IdeaExpandedDescriptionMaxLength = 20000;
	public const int IdeaExpansionErrorMaxLength = 1000;
	public const int IdeaAttachmentMaxCount = 10;
	public const long IdeaAttachmentMaxFileBytes = 15L * 1024 * 1024;
	public const long IdeaAttachmentMaxTotalBytes = 50L * 1024 * 1024;
	public const int IdeaAttachmentFileNameMaxLength = 255;
	public const int IdeaAttachmentContentTypeMaxLength = 200;
	public const int IdeaAttachmentRelativePathMaxLength = 500;
	public const int CriticalErrorLogFieldMaxLength = 100;
	public const int CriticalErrorLogMessageMaxLength = 1000;
	public const int CriticalErrorLogDetailsMaxLength = 8000;
	public const int CriticalErrorLogTraceIdMaxLength = 200;
	public const int CriticalErrorLogUrlMaxLength = 2000;
	public const int CriticalErrorLogUserAgentMaxLength = 1000;
	public const int CriticalErrorLogMetadataMaxLength = 4000;
}
