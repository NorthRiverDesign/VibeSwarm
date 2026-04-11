using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Identity;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class DatabaseServiceTests : IDisposable
{
	private readonly string _databasePath;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public DatabaseServiceTests()
	{
		_databasePath = Path.Combine(Path.GetTempPath(), $"vibeswarm-db-tests-{Guid.NewGuid():N}.db");
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite($"Data Source={_databasePath}")
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureDeleted();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task GetStorageSummaryAsync_ReturnsSqliteMetricsAndCleanupCounts()
	{
		await SeedDatabaseAsync();

		await using var dbContext = CreateDbContext();
		var service = CreateService(dbContext);

		var summary = await service.GetStorageSummaryAsync();

		Assert.Equal("sqlite", summary.Provider);
		Assert.True(summary.TotalSizeBytes > 0);
		Assert.True(summary.DataFileSizeBytes > 0);
		Assert.True(summary.SupportsCompaction);
		Assert.Equal(2, summary.JobsCount);
		Assert.Equal(2, summary.JobMessagesCount);
		Assert.Equal(2, summary.ProviderUsageRecordsCount);
		Assert.Equal(2, summary.CriticalErrorLogsCount);
		Assert.Equal(1, summary.CompletedJobsOlderThanRetentionCount);
		Assert.Equal(1, summary.ProviderUsageRecordsOlderThanRetentionCount);
		Assert.Equal(Path.GetFullPath(_databasePath), summary.Location);
	}

	[Fact]
	public async Task RunMaintenanceAsync_PrunesConfiguredDataAndCompactsDatabase()
	{
		await SeedDatabaseAsync();

		await using (var dbContext = CreateDbContext())
		{
			var service = CreateService(dbContext);
			var result = await service.RunMaintenanceAsync(new DatabaseMaintenanceRequest
			{
				Operation = DatabaseMaintenanceOperation.ApplyCriticalErrorLogRetention
			});

			Assert.Equal(DatabaseMaintenanceOperation.ApplyCriticalErrorLogRetention, result.Operation);
			Assert.Equal(1, result.AffectedRows);
			Assert.NotNull(result.SizeBeforeBytes);
			Assert.NotNull(result.SizeAfterBytes);
		}

		await using (var dbContext = CreateDbContext())
		{
			var service = CreateService(dbContext);
			var result = await service.RunMaintenanceAsync(new DatabaseMaintenanceRequest
			{
				Operation = DatabaseMaintenanceOperation.DeleteCompletedJobsOlderThanRetention
			});

			Assert.Equal(1, result.AffectedRows);
		}

		await using (var dbContext = CreateDbContext())
		{
			var service = CreateService(dbContext);
			var result = await service.RunMaintenanceAsync(new DatabaseMaintenanceRequest
			{
				Operation = DatabaseMaintenanceOperation.DeleteProviderUsageOlderThanRetention
			});

			Assert.Equal(1, result.AffectedRows);
		}

		await using (var dbContext = CreateDbContext())
		{
			var service = CreateService(dbContext);
			var result = await service.RunMaintenanceAsync(new DatabaseMaintenanceRequest
			{
				Operation = DatabaseMaintenanceOperation.CompactDatabase
			});

			Assert.Equal(DatabaseMaintenanceOperation.CompactDatabase, result.Operation);
			Assert.NotNull(result.SizeBeforeBytes);
			Assert.NotNull(result.SizeAfterBytes);
		}

		await using var verificationContext = CreateDbContext();
		Assert.Single(await verificationContext.CriticalErrorLogs.ToListAsync());
		Assert.Single(await verificationContext.Jobs.ToListAsync());
		Assert.Single(await verificationContext.JobMessages.ToListAsync());
		Assert.Single(await verificationContext.ProviderUsageRecords.ToListAsync());
	}

	[Fact]
	public async Task MigrateAsync_CopiesFullDatabaseAndWritesRuntimeConfiguration()
	{
		await SeedDatabaseAsync();
		var targetDatabasePath = Path.Combine(Path.GetTempPath(), $"vibeswarm-db-target-{Guid.NewGuid():N}.db");
		var runtimeConfigurationPath = Path.Combine(Path.GetTempPath(), $"vibeswarm-db-config-{Guid.NewGuid():N}.json");

		try
		{
			await using (var dbContext = CreateDbContext())
			{
				var service = CreateService(dbContext, runtimeConfigurationPath);
				var result = await service.MigrateAsync(new DatabaseMigrationRequest
				{
					Provider = "sqlite",
					ConnectionString = $"Data Source={targetDatabasePath}"
				});

				Assert.Equal("sqlite", result.Provider);
				Assert.True(result.CopiedTableCount > 0);
				Assert.True(result.CopiedRowCount > 0);
				Assert.Contains("Restart VibeSwarm", result.Message);
			}

			var targetOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
				.UseSqlite($"Data Source={targetDatabasePath}")
				.Options;
			await using var targetContext = new VibeSwarmDbContext(targetOptions);

			Assert.Equal(1, await targetContext.Users.CountAsync());
			Assert.Equal(1, await targetContext.Roles.CountAsync());
			Assert.Equal(1, await targetContext.UserRoles.CountAsync());
			Assert.Equal(1, await targetContext.Providers.CountAsync());
			Assert.Equal(2, await targetContext.Jobs.CountAsync());
			Assert.Equal(2, await targetContext.JobMessages.CountAsync());
			Assert.Equal(2, await targetContext.ProviderUsageRecords.CountAsync());
			Assert.Equal(1, await targetContext.AppSettings.CountAsync());
			Assert.Equal("secret-api-key", await targetContext.Providers.Select(provider => provider.ApiKey).SingleAsync());

			var runtimeConfiguration = new DatabaseRuntimeConfigurationStore(runtimeConfigurationPath).Load();
			Assert.NotNull(runtimeConfiguration);
			Assert.Equal("sqlite", runtimeConfiguration!.Provider);
			Assert.Contains(targetDatabasePath, runtimeConfiguration.ConnectionString, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(targetDatabasePath);
			File.Delete($"{targetDatabasePath}-wal");
			File.Delete($"{targetDatabasePath}-shm");
			File.Delete(runtimeConfigurationPath);
		}
	}

	private async Task SeedDatabaseAsync()
	{
		await using var dbContext = CreateDbContext();
		dbContext.Jobs.RemoveRange(dbContext.Jobs);
		dbContext.JobMessages.RemoveRange(dbContext.JobMessages);
		dbContext.ProviderUsageRecords.RemoveRange(dbContext.ProviderUsageRecords);
		dbContext.CriticalErrorLogs.RemoveRange(dbContext.CriticalErrorLogs);
		dbContext.Projects.RemoveRange(dbContext.Projects);
		dbContext.Providers.RemoveRange(dbContext.Providers);
		dbContext.AppSettings.RemoveRange(dbContext.AppSettings);
		dbContext.UserRoles.RemoveRange(dbContext.UserRoles);
		dbContext.UserClaims.RemoveRange(dbContext.UserClaims);
		dbContext.UserLogins.RemoveRange(dbContext.UserLogins);
		dbContext.UserTokens.RemoveRange(dbContext.UserTokens);
		dbContext.RoleClaims.RemoveRange(dbContext.RoleClaims);
		dbContext.Users.RemoveRange(dbContext.Users);
		dbContext.Roles.RemoveRange(dbContext.Roles);
		await dbContext.SaveChangesAsync();

		var role = new IdentityRole<Guid>
		{
			Id = Guid.NewGuid(),
			Name = DatabaseSeeder.AdminRole,
			NormalizedName = DatabaseSeeder.AdminRole.ToUpperInvariant()
		};

		var user = new ApplicationUser
		{
			Id = Guid.NewGuid(),
			UserName = "admin",
			NormalizedUserName = "ADMIN",
			Email = "admin@vibeswarm.local",
			NormalizedEmail = "ADMIN@VIBESWARM.LOCAL",
			EmailConfirmed = true,
			IsActive = true,
			PasswordHash = "hash",
			SecurityStamp = "security-stamp",
			ConcurrencyStamp = "concurrency-stamp",
			CreatedAt = DateTime.UtcNow
		};

		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			ApiKey = "secret-api-key"
		};

		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Storage Tests",
			WorkingPath = "/tmp/storage-tests"
		};

		var oldCompletedJob = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Old completed job",
			Status = JobStatus.Completed,
			CreatedAt = DateTime.UtcNow.AddDays(-45),
			CompletedAt = DateTime.UtcNow.AddDays(-40)
		};

		var recentCompletedJob = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Recent completed job",
			Status = JobStatus.Completed,
			CreatedAt = DateTime.UtcNow.AddDays(-5),
			CompletedAt = DateTime.UtcNow.AddDays(-2)
		};

		dbContext.Roles.Add(role);
		dbContext.Users.Add(user);
		dbContext.UserRoles.Add(new IdentityUserRole<Guid>
		{
			UserId = user.Id,
			RoleId = role.Id
		});
		dbContext.Providers.Add(provider);
		dbContext.Projects.Add(project);
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			CriticalErrorLogRetentionDays = 1,
			CriticalErrorLogMaxEntries = 100
		});
		dbContext.Jobs.AddRange(oldCompletedJob, recentCompletedJob);
		dbContext.JobMessages.AddRange(
			new JobMessage
			{
				Id = Guid.NewGuid(),
				JobId = oldCompletedJob.Id,
				Role = MessageRole.Assistant,
				Content = "Old transcript",
				CreatedAt = DateTime.UtcNow.AddDays(-40)
			},
			new JobMessage
			{
				Id = Guid.NewGuid(),
				JobId = recentCompletedJob.Id,
				Role = MessageRole.Assistant,
				Content = "Recent transcript",
				CreatedAt = DateTime.UtcNow.AddDays(-2)
			});
		dbContext.ProviderUsageRecords.AddRange(
			new ProviderUsageRecord
			{
				Id = Guid.NewGuid(),
				ProviderId = provider.Id,
				JobId = oldCompletedJob.Id,
				ModelUsed = "gpt-5.4",
				RecordedAt = DateTime.UtcNow.AddDays(-45)
			},
			new ProviderUsageRecord
			{
				Id = Guid.NewGuid(),
				ProviderId = provider.Id,
				JobId = recentCompletedJob.Id,
				ModelUsed = "gpt-5.4",
				RecordedAt = DateTime.UtcNow.AddDays(-1)
			});
		dbContext.CriticalErrorLogs.AddRange(
			new CriticalErrorLogEntry
			{
				Id = Guid.NewGuid(),
				Source = "server",
				Category = "unhandled-exception",
				Message = "Expired entry",
				CreatedAt = DateTime.UtcNow.AddDays(-5)
			},
			new CriticalErrorLogEntry
			{
				Id = Guid.NewGuid(),
				Source = "server",
				Category = "unhandled-exception",
				Message = "Recent entry",
				CreatedAt = DateTime.UtcNow
			});

		await dbContext.SaveChangesAsync();
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static DatabaseService CreateService(VibeSwarmDbContext dbContext, string? runtimeConfigurationPath = null)
	{
		return new DatabaseService(
			dbContext,
			new CriticalErrorLogService(dbContext, NullLogger<CriticalErrorLogService>.Instance),
			new DatabaseRuntimeConfigurationStore(runtimeConfigurationPath));
	}

	public void Dispose()
	{
		File.Delete(_databasePath);
		File.Delete($"{_databasePath}-wal");
		File.Delete($"{_databasePath}-shm");
	}
}
