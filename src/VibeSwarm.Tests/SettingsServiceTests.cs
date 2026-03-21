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
			InjectEfficiencyRules = false
		});

		var saved = await dbContext.AppSettings.SingleAsync();
		Assert.Equal(updated.Id, saved.Id);
		Assert.Equal("/tmp/projects", saved.DefaultProjectsDirectory);
		Assert.Equal("UTC", saved.TimeZoneId);
		Assert.False(saved.EnablePromptStructuring);
		Assert.False(saved.InjectRepoMap);
		Assert.False(saved.InjectEfficiencyRules);
		Assert.NotNull(saved.UpdatedAt);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	public void Dispose()
	{
		_connection.Dispose();
	}
}
