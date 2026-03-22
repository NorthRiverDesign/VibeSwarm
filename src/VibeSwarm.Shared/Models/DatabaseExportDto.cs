using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

public class DatabaseExportDto
{
	public string ExportVersion { get; set; } = "1.0";
	public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
	public AppSettingsExportDto? Settings { get; set; }
	public List<ProjectExportDto> Projects { get; set; } = [];
	public List<SkillExportDto> Skills { get; set; } = [];
	public List<TeamRoleExportDto> TeamRoles { get; set; } = [];
	public List<ScheduleExportDto> Schedules { get; set; } = [];
}

public class AppSettingsExportDto
{
	public string? DefaultProjectsDirectory { get; set; }
	public string? TimeZoneId { get; set; }
	public bool EnablePromptStructuring { get; set; }
	public bool InjectRepoMap { get; set; }
	public bool InjectEfficiencyRules { get; set; }
}

public class ProjectExportDto
{
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public string WorkingPath { get; set; } = string.Empty;
	public string? GitHubRepository { get; set; }
	public AutoCommitMode AutoCommitMode { get; set; }
	public GitChangeDeliveryMode GitChangeDeliveryMode { get; set; }
	public string? DefaultTargetBranch { get; set; }
	public bool PlanningEnabled { get; set; }
	public string? PromptContext { get; set; }
	public bool IdeasAutoExpand { get; set; }
	public bool IdeasAutoCommit { get; set; }
	public bool EnableTeamSwarm { get; set; }
	public bool BuildVerificationEnabled { get; set; }
	public string? BuildCommand { get; set; }
	public string? TestCommand { get; set; }
	public bool IsActive { get; set; } = true;
	public List<IdeaExportDto> Ideas { get; set; } = [];
	public List<EnvironmentExportDto> Environments { get; set; } = [];
}

public class IdeaExportDto
{
	public string Description { get; set; } = string.Empty;
	public string? ExpandedDescription { get; set; }
	public IdeaExpansionStatus ExpansionStatus { get; set; }
	public int SortOrder { get; set; }
}

public class EnvironmentExportDto
{
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public string Url { get; set; } = string.Empty;
	public EnvironmentType Type { get; set; }
	public EnvironmentStage Stage { get; set; }
	public bool IsPrimary { get; set; }
	public bool IsEnabled { get; set; } = true;
	public int SortOrder { get; set; }
}

public class SkillExportDto
{
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public string Content { get; set; } = string.Empty;
	public bool IsEnabled { get; set; } = true;
}

public class TeamRoleExportDto
{
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public string? Responsibilities { get; set; }
	public string? DefaultModelId { get; set; }
	public bool IsEnabled { get; set; } = true;
	public List<string> SkillNames { get; set; } = [];
}

public class ScheduleExportDto
{
	public string ProjectName { get; set; } = string.Empty;
	public string ProviderName { get; set; } = string.Empty;
	public string Prompt { get; set; } = string.Empty;
	public string? ModelId { get; set; }
	public JobScheduleFrequency Frequency { get; set; }
	public int HourUtc { get; set; }
	public int MinuteUtc { get; set; }
	public DayOfWeek WeeklyDay { get; set; }
	public int DayOfMonth { get; set; } = 1;
	public bool IsEnabled { get; set; } = true;
}

public class DatabaseImportResult
{
	public List<string> Imported { get; set; } = [];
	public List<string> Skipped { get; set; } = [];
	public List<string> Errors { get; set; } = [];
	public bool HasErrors => Errors.Count > 0;
}
