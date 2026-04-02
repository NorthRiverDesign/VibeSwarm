using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Data;

public class VibeSwarmDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
	public VibeSwarmDbContext(DbContextOptions<VibeSwarmDbContext> options)
	: base(options)
	{
	}

	public DbSet<Provider> Providers { get; set; }
	public DbSet<ProviderModel> ProviderModels { get; set; }
	public DbSet<ProviderUsageRecord> ProviderUsageRecords { get; set; }
	public DbSet<ProviderUsageSummary> ProviderUsageSummaries { get; set; }
	public DbSet<Project> Projects { get; set; }
	public DbSet<ProjectProvider> ProjectProviders { get; set; }
	public DbSet<ProjectTeamRole> ProjectTeamRoles { get; set; }
	public DbSet<ProjectEnvironment> ProjectEnvironments { get; set; }
	public DbSet<JobSchedule> JobSchedules { get; set; }
	public DbSet<Job> Jobs { get; set; }
	public DbSet<JobMessage> JobMessages { get; set; }
	public DbSet<JobProviderAttempt> JobProviderAttempts { get; set; }
	public DbSet<Skill> Skills { get; set; }
	public DbSet<TeamRole> TeamRoles { get; set; }
	public DbSet<TeamRoleSkill> TeamRoleSkills { get; set; }
	public DbSet<Idea> Ideas { get; set; }
	public DbSet<IdeaAttachment> IdeaAttachments { get; set; }
	public DbSet<AppSettings> AppSettings { get; set; }
	public DbSet<CriticalErrorLogEntry> CriticalErrorLogs { get; set; }
	public DbSet<InferenceProvider> InferenceProviders { get; set; }
	public DbSet<InferenceModel> InferenceModels { get; set; }
	public DbSet<IterationLoop> IterationLoops { get; set; }

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.Entity<ApplicationUser>(entity =>
		{
			entity.Property(e => e.ThemePreference)
				.HasConversion<string>()
				.HasMaxLength(20)
				.HasDefaultValue(ThemePreference.System);
		});

		modelBuilder.Entity<Provider>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
			entity.Property(e => e.Type).HasConversion<string>();
			entity.Property(e => e.ConnectionMode).HasConversion<string>();
			entity.Property(e => e.ExecutablePath).HasMaxLength(500);
			entity.Property(e => e.WorkingDirectory).HasMaxLength(500);
			entity.Property(e => e.ApiEndpoint).HasMaxLength(500);
			entity.Property(e => e.ApiKey).HasMaxLength(200);
			entity.Property(e => e.DefaultReasoningEffort).HasMaxLength(ValidationLimits.ReasoningEffortMaxLength);
			entity.Property(e => e.ConfiguredLimitType).HasConversion<string>();
			entity.HasIndex(e => e.Name).IsUnique();
		});

		modelBuilder.Entity<ProviderModel>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.ModelId).IsRequired().HasMaxLength(200);
			entity.Property(e => e.DisplayName).HasMaxLength(200);
			entity.Property(e => e.Description).HasMaxLength(500);
			entity.HasOne(e => e.Provider)
	.WithMany(p => p.AvailableModels)
	.HasForeignKey(e => e.ProviderId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasIndex(e => new { e.ProviderId, e.ModelId }).IsUnique();
		});

		modelBuilder.Entity<ProviderUsageRecord>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.ModelUsed).HasMaxLength(200);
			entity.Property(e => e.DetectedLimitType).HasConversion<string>();
			entity.Property(e => e.RawLimitMessage).HasMaxLength(1000);
			entity.Property(e => e.DetectedLimitWindowsJson).HasMaxLength(4000);
			entity.HasOne(e => e.Provider)
	.WithMany()
	.HasForeignKey(e => e.ProviderId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.Job)
	.WithMany()
	.HasForeignKey(e => e.JobId)
	.OnDelete(DeleteBehavior.SetNull);
			entity.HasIndex(e => e.ProviderId);
			entity.HasIndex(e => e.RecordedAt);
		});

		modelBuilder.Entity<ProviderUsageSummary>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.LimitType).HasConversion<string>();
			entity.Property(e => e.LimitMessage).HasMaxLength(500);
			entity.Property(e => e.LimitWindowsJson).HasMaxLength(4000);
			entity.Property(e => e.CliVersion).HasMaxLength(50);
			entity.HasOne(e => e.Provider)
	.WithMany()
	.HasForeignKey(e => e.ProviderId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasIndex(e => e.ProviderId).IsUnique();
		});

		modelBuilder.Entity<Project>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Name).IsRequired().HasMaxLength(ValidationLimits.ProjectNameMaxLength);
			entity.Property(e => e.Description).HasMaxLength(ValidationLimits.ProjectDescriptionMaxLength);
			entity.Property(e => e.WorkingPath).IsRequired().HasMaxLength(ValidationLimits.ProjectWorkingPathMaxLength);
			entity.Property(e => e.GitHubRepository).HasMaxLength(ValidationLimits.ProjectGitHubRepositoryMaxLength);
			entity.Property(e => e.DefaultTargetBranch).HasMaxLength(ValidationLimits.ProjectDefaultTargetBranchMaxLength);
			entity.Property(e => e.PlanningModelId).HasMaxLength(ValidationLimits.ProjectPlanningModelIdMaxLength);
			entity.Property(e => e.PlanningReasoningEffort).HasMaxLength(ValidationLimits.ReasoningEffortMaxLength);
			entity.Property(e => e.IdeaInferenceModelId).HasMaxLength(200);
			entity.Property(e => e.PromptContext).HasMaxLength(ValidationLimits.ProjectPromptContextMaxLength);
			entity.Property(e => e.Memory).HasMaxLength(ValidationLimits.ProjectMemoryMaxLength);
			entity.Property(e => e.BuildCommand).HasMaxLength(500);
			entity.Property(e => e.TestCommand).HasMaxLength(500);
			entity.HasIndex(e => e.Name).IsUnique();
		});

		modelBuilder.Entity<ProjectProvider>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasOne(e => e.Project)
	.WithMany(p => p.ProviderSelections)
	.HasForeignKey(e => e.ProjectId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.Provider)
	.WithMany()
	.HasForeignKey(e => e.ProviderId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.Property(e => e.PreferredModelId).HasMaxLength(200);
			entity.Property(e => e.PreferredReasoningEffort).HasMaxLength(ValidationLimits.ReasoningEffortMaxLength);
			entity.HasIndex(e => new { e.ProjectId, e.ProviderId }).IsUnique();
			entity.HasIndex(e => new { e.ProjectId, e.Priority });
		});

		modelBuilder.Entity<ProjectTeamRole>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasOne(e => e.Project)
	.WithMany(project => project.TeamAssignments)
	.HasForeignKey(e => e.ProjectId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.TeamRole)
	.WithMany(role => role.ProjectAssignments)
	.HasForeignKey(e => e.TeamRoleId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.Provider)
	.WithMany()
	.HasForeignKey(e => e.ProviderId)
	.OnDelete(DeleteBehavior.Restrict);
			entity.Property(e => e.PreferredModelId).HasMaxLength(200);
			entity.Property(e => e.PreferredReasoningEffort).HasMaxLength(ValidationLimits.ReasoningEffortMaxLength);
			entity.HasIndex(e => new { e.ProjectId, e.TeamRoleId }).IsUnique();
		});

		modelBuilder.Entity<ProjectEnvironment>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasOne(e => e.Project)
	.WithMany(p => p.Environments)
			.HasForeignKey(e => e.ProjectId)
			.OnDelete(DeleteBehavior.Cascade);
			entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
			entity.Property(e => e.Description).HasMaxLength(1000);
			entity.Property(e => e.Url).IsRequired().HasMaxLength(1000);
			entity.Property(e => e.Type).HasConversion<string>();
			entity.Property(e => e.Stage).HasConversion<string>();
			entity.Property(e => e.UsernameCiphertext).HasMaxLength(4000);
			entity.Property(e => e.PasswordCiphertext).HasMaxLength(4000);
			entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
			entity.HasIndex(e => new { e.ProjectId, e.SortOrder });
		});

		modelBuilder.Entity<JobSchedule>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Prompt).IsRequired().HasMaxLength(ValidationLimits.JobSchedulePromptMaxLength);
			entity.Property(e => e.ModelId).HasMaxLength(ValidationLimits.JobScheduleModelIdMaxLength);
			entity.Property(e => e.ScheduleType).HasConversion<string>();
			entity.Property(e => e.ExecutionTarget).HasConversion<string>();
			entity.Property(e => e.Frequency).HasConversion<string>();
			entity.Property(e => e.WeeklyDay).HasConversion<string>();
			entity.Property(e => e.LastError).HasMaxLength(ValidationLimits.JobScheduleLastErrorMaxLength);
			entity.HasOne(e => e.Project)
				.WithMany()
				.HasForeignKey(e => e.ProjectId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.Provider)
				.WithMany()
				.HasForeignKey(e => e.ProviderId)
				.OnDelete(DeleteBehavior.SetNull);
			entity.HasOne(e => e.TeamRole)
				.WithMany()
				.HasForeignKey(e => e.TeamRoleId)
				.OnDelete(DeleteBehavior.SetNull);
			entity.HasOne(e => e.InferenceProvider)
				.WithMany()
				.HasForeignKey(e => e.InferenceProviderId)
				.OnDelete(DeleteBehavior.SetNull);
			entity.HasIndex(e => e.InferenceProviderId);
			entity.HasIndex(e => e.ProviderId);
			entity.HasIndex(e => e.TeamRoleId);
			entity.HasIndex(e => new { e.IsEnabled, e.NextRunAtUtc });
			entity.HasIndex(e => new { e.ProjectId, e.IsEnabled });
		});

		modelBuilder.Entity<Job>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Title).HasMaxLength(200);
			entity.Property(e => e.GoalPrompt).IsRequired().HasMaxLength(2000);
			entity.Property(e => e.AttachedFilesJson).HasMaxLength(4000);
			entity.Property(e => e.ModelUsed).HasMaxLength(200);
			entity.Property(e => e.ReasoningEffort).HasMaxLength(ValidationLimits.ReasoningEffortMaxLength);
			entity.Property(e => e.PlanningModelUsed).HasMaxLength(200);
			entity.Property(e => e.PlanningReasoningEffortUsed).HasMaxLength(ValidationLimits.ReasoningEffortMaxLength);
			entity.Property(e => e.LastSwitchReason).HasMaxLength(200);
			entity.Property(e => e.Branch).HasMaxLength(250);
			entity.Property(e => e.TargetBranch).HasMaxLength(250);
			entity.Property(e => e.GitCheckpointBranch).HasMaxLength(250);
			entity.Property(e => e.GitCheckpointBaseBranch).HasMaxLength(250);
			entity.Property(e => e.GitCheckpointCommitHash).HasMaxLength(100);
			entity.Property(e => e.GitCheckpointReason).HasMaxLength(500);
			entity.Property(e => e.SessionId).HasMaxLength(200);
			entity.Property(e => e.CommandUsed).HasMaxLength(4000);
			entity.Property(e => e.PlanningCommandUsed).HasMaxLength(4000);
			entity.Property(e => e.ExecutionCommandUsed).HasMaxLength(4000);
			entity.HasOne(e => e.JobSchedule)
				.WithMany(schedule => schedule.Jobs)
				.HasForeignKey(e => e.JobScheduleId)
				.OnDelete(DeleteBehavior.SetNull);
			entity.HasOne(e => e.Project)
	.WithMany(p => p.Jobs)
	.HasForeignKey(e => e.ProjectId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.Provider)
	.WithMany()
	.HasForeignKey(e => e.ProviderId)
	.OnDelete(DeleteBehavior.Restrict);
			entity.HasOne(e => e.TeamRole)
				.WithMany()
				.HasForeignKey(e => e.TeamRoleId)
				.OnDelete(DeleteBehavior.SetNull);
			entity.HasIndex(e => e.Status);
			entity.HasIndex(e => e.CreatedAt);
			entity.HasIndex(e => e.SwarmId);
			entity.HasIndex(e => new { e.JobScheduleId, e.ScheduledForUtc }).IsUnique();
		});

		modelBuilder.Entity<JobMessage>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Role).HasConversion<string>();
			entity.Property(e => e.Content).IsRequired();
			entity.Property(e => e.ToolName).HasMaxLength(200);
			entity.HasOne(e => e.Job)
	.WithMany(j => j.Messages)
	.HasForeignKey(e => e.JobId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasIndex(e => e.JobId);
			entity.HasIndex(e => e.CreatedAt);
		});

		modelBuilder.Entity<JobProviderAttempt>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.ProviderName).HasMaxLength(100);
			entity.Property(e => e.ModelId).HasMaxLength(200);
			entity.Property(e => e.Reason).HasMaxLength(100);
			entity.HasOne(e => e.Job)
	.WithMany(j => j.ProviderAttempts)
	.HasForeignKey(e => e.JobId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasIndex(e => e.JobId);
			entity.HasIndex(e => new { e.JobId, e.AttemptOrder });
		});

		modelBuilder.Entity<Skill>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
			entity.Property(e => e.Description).HasMaxLength(500);
			entity.Property(e => e.Content).IsRequired();
			entity.HasIndex(e => e.Name).IsUnique();
		});

		modelBuilder.Entity<TeamRole>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
			entity.Property(e => e.Description).HasMaxLength(ValidationLimits.TeamRoleDescriptionMaxLength);
			entity.Property(e => e.Responsibilities).HasMaxLength(ValidationLimits.TeamRoleResponsibilitiesMaxLength);
			entity.Property(e => e.DefaultModelId).HasMaxLength(200);
			entity.Property(e => e.DefaultReasoningEffort).HasMaxLength(ValidationLimits.ReasoningEffortMaxLength);
			entity.HasOne(e => e.DefaultProvider)
				.WithMany()
				.HasForeignKey(e => e.DefaultProviderId)
				.OnDelete(DeleteBehavior.SetNull);
			entity.HasIndex(e => e.Name).IsUnique();
		});

		modelBuilder.Entity<TeamRoleSkill>(entity =>
		{
			entity.HasKey(e => new { e.TeamRoleId, e.SkillId });
			entity.HasOne(e => e.TeamRole)
	.WithMany(role => role.SkillLinks)
	.HasForeignKey(e => e.TeamRoleId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.Skill)
	.WithMany()
	.HasForeignKey(e => e.SkillId)
	.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<Idea>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Description).IsRequired().HasMaxLength(ValidationLimits.IdeaDescriptionMaxLength);
			entity.Property(e => e.ExpandedDescription).HasMaxLength(ValidationLimits.IdeaExpandedDescriptionMaxLength);
			entity.Property(e => e.ExpansionStatus).HasConversion<string>();
			entity.Property(e => e.ExpansionError).HasMaxLength(ValidationLimits.IdeaExpansionErrorMaxLength);
			entity.HasOne(e => e.Project)
	.WithMany(p => p.Ideas)
	.HasForeignKey(e => e.ProjectId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.Job)
	.WithMany()
	.HasForeignKey(e => e.JobId)
	.OnDelete(DeleteBehavior.SetNull);
			entity.HasIndex(e => e.ProjectId);
			entity.HasIndex(e => e.SortOrder);
			entity.HasIndex(e => e.CreatedAt);
		});

		modelBuilder.Entity<IdeaAttachment>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.FileName).IsRequired().HasMaxLength(ValidationLimits.IdeaAttachmentFileNameMaxLength);
			entity.Property(e => e.ContentType).HasMaxLength(ValidationLimits.IdeaAttachmentContentTypeMaxLength);
			entity.Property(e => e.RelativePath).IsRequired().HasMaxLength(ValidationLimits.IdeaAttachmentRelativePathMaxLength);
			entity.HasOne(e => e.Idea)
				.WithMany(idea => idea.Attachments)
				.HasForeignKey(e => e.IdeaId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasIndex(e => e.IdeaId);
		});

		modelBuilder.Entity<AppSettings>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.DefaultProjectsDirectory).HasMaxLength(1000);
			entity.Property(e => e.TimeZoneId).IsRequired().HasMaxLength(100).HasDefaultValue(DateTimeHelper.UtcTimeZoneId);
			entity.Property(e => e.EnablePromptStructuring).HasDefaultValue(true);
			entity.Property(e => e.InjectRepoMap).HasDefaultValue(true);
			entity.Property(e => e.InjectEfficiencyRules).HasDefaultValue(true);
			entity.Property(e => e.EnableCommitAttribution).HasDefaultValue(true);
			entity.Property(e => e.CriticalErrorLogRetentionDays).HasDefaultValue(global::VibeSwarm.Shared.Data.AppSettings.DefaultCriticalErrorLogRetentionDays);
			entity.Property(e => e.CriticalErrorLogMaxEntries).HasDefaultValue(global::VibeSwarm.Shared.Data.AppSettings.DefaultCriticalErrorLogMaxEntries);
		});

		modelBuilder.Entity<CriticalErrorLogEntry>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Source).IsRequired().HasMaxLength(ValidationLimits.CriticalErrorLogFieldMaxLength);
			entity.Property(e => e.Category).IsRequired().HasMaxLength(ValidationLimits.CriticalErrorLogFieldMaxLength);
			entity.Property(e => e.Severity).IsRequired().HasMaxLength(ValidationLimits.CriticalErrorLogFieldMaxLength);
			entity.Property(e => e.Message).IsRequired().HasMaxLength(ValidationLimits.CriticalErrorLogMessageMaxLength);
			entity.Property(e => e.Details).HasMaxLength(ValidationLimits.CriticalErrorLogDetailsMaxLength);
			entity.Property(e => e.TraceId).HasMaxLength(ValidationLimits.CriticalErrorLogTraceIdMaxLength);
			entity.Property(e => e.Url).HasMaxLength(ValidationLimits.CriticalErrorLogUrlMaxLength);
			entity.Property(e => e.UserAgent).HasMaxLength(ValidationLimits.CriticalErrorLogUserAgentMaxLength);
			entity.Property(e => e.RefreshAction).HasMaxLength(ValidationLimits.CriticalErrorLogFieldMaxLength);
			entity.Property(e => e.AdditionalDataJson).HasMaxLength(ValidationLimits.CriticalErrorLogMetadataMaxLength);
			entity.HasIndex(e => e.CreatedAt);
			entity.HasIndex(e => new { e.Source, e.CreatedAt });
		});

		modelBuilder.Entity<InferenceProvider>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
			entity.Property(e => e.ProviderType).HasConversion<string>();
			entity.Property(e => e.Endpoint).IsRequired().HasMaxLength(500);
			entity.Property(e => e.ApiKey).HasMaxLength(200);
			entity.HasIndex(e => e.Name).IsUnique();
		});

		modelBuilder.Entity<InferenceModel>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.ModelId).IsRequired().HasMaxLength(200);
			entity.Property(e => e.DisplayName).HasMaxLength(200);
			entity.Property(e => e.ParameterSize).HasMaxLength(50);
			entity.Property(e => e.Family).HasMaxLength(100);
			entity.Property(e => e.QuantizationLevel).HasMaxLength(50);
			entity.Property(e => e.TaskType).IsRequired().HasMaxLength(100);
			entity.HasOne(e => e.InferenceProvider)
	.WithMany(p => p.Models)
	.HasForeignKey(e => e.InferenceProviderId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasIndex(e => new { e.InferenceProviderId, e.ModelId, e.TaskType }).IsUnique();
		});

		modelBuilder.Entity<IterationLoop>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Status).HasConversion<string>();
			entity.Property(e => e.ModelId).HasMaxLength(200);
			entity.Property(e => e.InferenceModelId).HasMaxLength(200);
			entity.Property(e => e.LastStopReason).HasMaxLength(500);
			entity.HasOne(e => e.Project)
	.WithMany()
	.HasForeignKey(e => e.ProjectId)
	.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.CurrentJob)
	.WithMany()
	.HasForeignKey(e => e.CurrentJobId)
	.OnDelete(DeleteBehavior.SetNull);
			entity.HasIndex(e => e.ProjectId);
			entity.HasIndex(e => e.Status);
		});
	}
}
