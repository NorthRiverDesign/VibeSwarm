using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class CriticalErrorLogServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public CriticalErrorLogServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task LogAsync_PrunesOldestEntries_WhenCapacityExceeded()
	{
		await using var dbContext = CreateDbContext();
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			CriticalErrorLogRetentionDays = 30,
			CriticalErrorLogMaxEntries = 10
		});
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext);

		for (var index = 0; index < 11; index++)
		{
			await service.LogAsync(new CriticalErrorLogEntry
			{
				Source = "client",
				Category = "ui-error-boundary",
				Message = $"Fatal error {index + 1}",
				CreatedAt = DateTime.UtcNow.AddMinutes(-(11 - index))
			});
		}

		var entries = await dbContext.CriticalErrorLogs
			.OrderBy(entry => entry.CreatedAt)
			.ToListAsync();

		Assert.Equal(10, entries.Count);
		Assert.Equal("Fatal error 2", entries[0].Message);
		Assert.Equal("Fatal error 11", entries[^1].Message);
	}

	[Fact]
	public async Task ApplyRetentionPolicyAsync_RemovesExpiredEntries()
	{
		await using var dbContext = CreateDbContext();
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			CriticalErrorLogRetentionDays = 1,
			CriticalErrorLogMaxEntries = 50
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

		var service = CreateService(dbContext);
		await service.ApplyRetentionPolicyAsync();

		var entries = await dbContext.CriticalErrorLogs.OrderBy(entry => entry.CreatedAt).ToListAsync();

		Assert.Single(entries);
		Assert.Equal("Recent entry", entries[0].Message);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static CriticalErrorLogService CreateService(VibeSwarmDbContext dbContext)
	{
		return new CriticalErrorLogService(dbContext, NullLogger<CriticalErrorLogService>.Instance);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}
}
