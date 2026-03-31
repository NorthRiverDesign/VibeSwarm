using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class SettingsServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public SettingsServiceTests()
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
	public async Task UpdateSettingsAsync_PersistsTimezoneAndAgentQualityFlags()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var updated = await service.UpdateSettingsAsync(new AppSettings
		{
			DefaultProjectsDirectory = "/tmp/projects",
			TimeZoneId = "UTC",
			EnablePromptStructuring = false,
			InjectRepoMap = false,
			InjectEfficiencyRules = false,
			CriticalErrorLogRetentionDays = 45,
			CriticalErrorLogMaxEntries = 350
		});

		var saved = await dbContext.AppSettings.SingleAsync();
		Assert.Equal(updated.Id, saved.Id);
		Assert.Equal("/tmp/projects", saved.DefaultProjectsDirectory);
		Assert.Equal("UTC", saved.TimeZoneId);
		Assert.False(saved.EnablePromptStructuring);
		Assert.False(saved.InjectRepoMap);
		Assert.False(saved.InjectEfficiencyRules);
		Assert.Equal(45, saved.CriticalErrorLogRetentionDays);
		Assert.Equal(350, saved.CriticalErrorLogMaxEntries);
		Assert.NotNull(saved.UpdatedAt);
	}

	[Fact]
	public async Task GetSettingsAsync_NormalizesCriticalErrorLogCapacity()
	{
		await using var dbContext = CreateDbContext();
		dbContext.AppSettings.Add(new AppSettings
		{
			Id = Guid.NewGuid(),
			TimeZoneId = "UTC",
			CriticalErrorLogRetentionDays = AppSettings.MaxCriticalErrorLogRetentionDays + 20,
			CriticalErrorLogMaxEntries = AppSettings.MaxCriticalErrorLogMaxEntries + 500
		});
		await dbContext.SaveChangesAsync();

		var service = new SettingsService(dbContext);
		var settings = await service.GetSettingsAsync();

		Assert.Equal(AppSettings.MaxCriticalErrorLogRetentionDays, settings.CriticalErrorLogRetentionDays);
		Assert.Equal(AppSettings.MaxCriticalErrorLogMaxEntries, settings.CriticalErrorLogMaxEntries);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	public void Dispose()
	{
		_connection.Dispose();
	}
}
