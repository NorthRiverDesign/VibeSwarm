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

    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ProviderModel> ProviderModels => Set<ProviderModel>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobMessage> JobMessages => Set<JobMessage>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Provider>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ExecutablePath).HasMaxLength(500);
            entity.Property(e => e.WorkingDirectory).HasMaxLength(500);
            entity.Property(e => e.ApiEndpoint).HasMaxLength(500);
            entity.Property(e => e.ApiKey).HasMaxLength(200);
            entity.Property(e => e.Type).HasConversion<string>();
            entity.Property(e => e.ConnectionMode).HasConversion<string>();
            entity.HasMany(e => e.AvailableModels)
                .WithOne(m => m.Provider)
                .HasForeignKey(m => m.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderModel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ModelId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.PriceMultiplier).HasPrecision(18, 4);
            entity.HasIndex(e => new { e.ProviderId, e.ModelId }).IsUnique();
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.WorkingPath).IsRequired().HasMaxLength(500);
            entity.HasMany(e => e.Jobs)
                .WithOne(j => j.Project)
                .HasForeignKey(j => j.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GoalPrompt).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.SessionId).HasMaxLength(100);
            entity.Property(e => e.ModelUsed).HasMaxLength(200);
            entity.Property(e => e.TotalCostUsd).HasPrecision(18, 6);
            entity.Property(e => e.MaxCostUsd).HasPrecision(18, 6);
            entity.Property(e => e.Tags).HasMaxLength(500);
            entity.Property(e => e.SuccessPattern).HasMaxLength(1000);
            entity.Property(e => e.FailurePattern).HasMaxLength(1000);
            entity.Property(e => e.GitCommitBefore).HasMaxLength(100);
            entity.Property(e => e.GitCommitHash).HasMaxLength(100);
            // GitDiff and ConsoleOutput can be large, no max length constraint
            entity.HasOne(e => e.Provider)
                .WithMany()
                .HasForeignKey(e => e.ProviderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(e => e.Messages)
                .WithOne(m => m.Job)
                .HasForeignKey(m => m.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            // Index for efficient querying
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => new { e.Status, e.Priority, e.CreatedAt });
            entity.HasIndex(e => e.ParentJobId);
            entity.HasIndex(e => e.DependsOnJobId);
        });

        modelBuilder.Entity<JobMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasConversion<string>();
            entity.Property(e => e.Content).IsRequired();
        });

        modelBuilder.Entity<AppSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DefaultProjectsDirectory).HasMaxLength(1000);
        });
    }
}
