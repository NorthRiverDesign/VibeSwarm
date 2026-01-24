using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Shared.Data;

public class VibeSwarmDbContext : DbContext
{
    public VibeSwarmDbContext(DbContextOptions<VibeSwarmDbContext> options)
        : base(options)
    {
    }

    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobMessage> JobMessages => Set<JobMessage>();

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
            entity.Property(e => e.TotalCostUsd).HasPrecision(18, 6);
            entity.Property(e => e.MaxCostUsd).HasPrecision(18, 6);
            entity.Property(e => e.Tags).HasMaxLength(500);
            entity.Property(e => e.SuccessPattern).HasMaxLength(1000);
            entity.Property(e => e.FailurePattern).HasMaxLength(1000);
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
    }
}
