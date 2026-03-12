using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Services;

public class VibeSwarmDbContext : IdentityDbContext<ApplicationUser>
{
	public VibeSwarmDbContext(DbContextOptions<VibeSwarmDbContext> options)
		: base(options)
	{
	}

	public DbSet<Provider> Providers { get; set; }
	public DbSet<Project> Projects { get; set; }
	public DbSet<ProjectProvider> ProjectProviders { get; set; }
	public DbSet<ProjectEnvironment> ProjectEnvironments { get; set; }
	public DbSet<Job> Jobs { get; set; }
	public DbSet<JobExecution> JobExecutions { get; set; }
	public DbSet<Skill> Skills { get; set; }
	public DbSet<Idea> Ideas { get; set; }

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.Entity<Provider>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
			entity.Property(e => e.Description).HasMaxLength(500);
			entity.Property(e => e.BaseUrl).HasMaxLength(500);
			entity.Property(e => e.ApiKeyEncrypted).HasMaxLength(1000);
			entity.Property(e => e.SecretEncrypted).HasMaxLength(1000);
			entity.Property(e => e.CurrentMcpConfigPath).HasMaxLength(1000);
			entity.HasIndex(e => e.Name).IsUnique();
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
			entity.Property(e => e.SortOrder).HasDefaultValue(0);
			entity.Property(e => e.UsernameCiphertext).HasMaxLength(4000);
			entity.Property(e => e.PasswordCiphertext).HasMaxLength(4000);
			entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
			entity.HasIndex(e => new { e.ProjectId, e.SortOrder });
		});

		modelBuilder.Entity<Job>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.GoalPrompt).IsRequired();
			entity.Property(e => e.Branch).HasMaxLength(200);
			entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
			entity.Property(e => e.CommitSha).HasMaxLength(200);
			entity.Property(e => e.SessionId).HasMaxLength(200);
			entity.Property(e => e.ProviderMetadataJson);
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

		modelBuilder.Entity<JobExecution>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.Property(e => e.ProviderName).IsRequired().HasMaxLength(100);
			entity.Property(e => e.ProviderType).HasConversion<string>().HasMaxLength(50);
			entity.Property(e => e.ModelId).HasMaxLength(100);
			entity.Property(e => e.StartedAt).IsRequired();
			entity.Property(e => e.CompletedAt);
			entity.Property(e => e.ExitCode);
			entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
			entity.Property(e => e.SessionId).HasMaxLength(200);
			entity.Property(e => e.ProviderMetadataJson);
			entity.HasOne(e => e.Job)
				.WithMany(j => j.Executions)
				.HasForeignKey(e => e.JobId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasIndex(e => e.JobId);
			entity.HasIndex(e => e.StartedAt);
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
			entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
			entity.Property(e => e.Description);
			entity.Property(e => e.Analysis).HasMaxLength(4000);
			entity.Property(e => e.FailureReason).HasMaxLength(1000);
			entity.Property(e => e.BranchName).HasMaxLength(200);
			entity.Property(e => e.GeneratedPrompt);
			entity.HasOne(e => e.Project)
				.WithMany(p => p.Ideas)
				.HasForeignKey(e => e.ProjectId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(e => e.AssignedJob)
				.WithMany()
				.HasForeignKey(e => e.AssignedJobId)
				.OnDelete(DeleteBehavior.SetNull);
			entity.HasIndex(e => e.ProjectId);
			entity.HasIndex(e => e.Status);
			entity.HasIndex(e => e.Priority);
			entity.HasIndex(e => e.CreatedAt);
		});
	}
}
