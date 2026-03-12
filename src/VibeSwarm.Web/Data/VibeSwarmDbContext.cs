using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Providers;

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
public DbSet<ProjectEnvironment> ProjectEnvironments { get; set; }
public DbSet<Job> Jobs { get; set; }
public DbSet<JobMessage> JobMessages { get; set; }
public DbSet<JobProviderAttempt> JobProviderAttempts { get; set; }
public DbSet<Skill> Skills { get; set; }
public DbSet<Idea> Ideas { get; set; }
public DbSet<AppSettings> AppSettings { get; set; }
public DbSet<InferenceProvider> InferenceProviders { get; set; }
public DbSet<InferenceModel> InferenceModels { get; set; }

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
base.OnModelCreating(modelBuilder);

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
entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
entity.Property(e => e.Description).HasMaxLength(500);
entity.Property(e => e.WorkingPath).IsRequired().HasMaxLength(500);
entity.Property(e => e.GitHubRepository).HasMaxLength(200);
entity.Property(e => e.PromptContext).HasMaxLength(1000);
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
entity.HasIndex(e => new { e.ProjectId, e.ProviderId }).IsUnique();
entity.HasIndex(e => new { e.ProjectId, e.Priority });
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
entity.Property(e => e.IsEnabled).HasDefaultValue(true);
entity.Property(e => e.IsPrimary).HasDefaultValue(false);
entity.Property(e => e.SortOrder).HasDefaultValue(0);
entity.Property(e => e.UsernameCiphertext).HasMaxLength(4000);
entity.Property(e => e.PasswordCiphertext).HasMaxLength(4000);
entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
entity.HasIndex(e => new { e.ProjectId, e.SortOrder });
});

modelBuilder.Entity<Job>(entity =>
{
entity.HasKey(e => e.Id);
entity.Property(e => e.Title).HasMaxLength(200);
entity.Property(e => e.GoalPrompt).IsRequired().HasMaxLength(2000);
entity.Property(e => e.ModelUsed).HasMaxLength(200);
entity.Property(e => e.LastSwitchReason).HasMaxLength(200);
entity.Property(e => e.Branch).HasMaxLength(250);
entity.Property(e => e.SessionId).HasMaxLength(200);
entity.Property(e => e.CommandUsed).HasMaxLength(4000);
entity.HasOne(e => e.Project)
.WithMany(p => p.Jobs)
.HasForeignKey(e => e.ProjectId)
.OnDelete(DeleteBehavior.Cascade);
entity.HasOne(e => e.Provider)
.WithMany()
.HasForeignKey(e => e.ProviderId)
.OnDelete(DeleteBehavior.Restrict);
entity.HasIndex(e => e.Status);
entity.HasIndex(e => e.CreatedAt);
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

modelBuilder.Entity<Idea>(entity =>
{
entity.HasKey(e => e.Id);
entity.Property(e => e.Description).IsRequired().HasMaxLength(2000);
entity.Property(e => e.ExpandedDescription).HasMaxLength(10000);
entity.Property(e => e.ExpansionStatus).HasConversion<string>();
entity.Property(e => e.ExpansionError).HasMaxLength(1000);
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

modelBuilder.Entity<AppSettings>(entity =>
{
entity.HasKey(e => e.Id);
entity.Property(e => e.DefaultProjectsDirectory).HasMaxLength(1000);
entity.Property(e => e.EnablePromptStructuring).HasDefaultValue(true);
entity.Property(e => e.InjectRepoMap).HasDefaultValue(true);
entity.Property(e => e.InjectEfficiencyRules).HasDefaultValue(true);
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
}
}
