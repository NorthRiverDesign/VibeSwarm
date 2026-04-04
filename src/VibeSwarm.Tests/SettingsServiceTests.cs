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
			EnableCommitAttribution = false,
			CriticalErrorLogRetentionDays = 45,
			CriticalErrorLogMaxEntries = 350,
			IdeaExpansionPromptTemplate = "Expand {{idea}}",
			IdeaImplementationPromptTemplate = "Implement {{idea}}",
			ApprovedIdeaImplementationPromptTemplate = "Idea {{idea}}\nSpec {{specification}}"
		});

		var saved = await dbContext.AppSettings.SingleAsync();
		Assert.Equal(updated.Id, saved.Id);
		Assert.Equal("/tmp/projects", saved.DefaultProjectsDirectory);
		Assert.Equal("UTC", saved.TimeZoneId);
		Assert.False(saved.EnablePromptStructuring);
		Assert.False(saved.InjectRepoMap);
		Assert.False(saved.InjectEfficiencyRules);
		Assert.False(saved.EnableCommitAttribution);
		Assert.Equal(45, saved.CriticalErrorLogRetentionDays);
		Assert.Equal(350, saved.CriticalErrorLogMaxEntries);
		Assert.Equal("Expand {{idea}}", saved.IdeaExpansionPromptTemplate);
		Assert.Equal("Implement {{idea}}", saved.IdeaImplementationPromptTemplate);
		Assert.Equal("Idea {{idea}}\nSpec {{specification}}", saved.ApprovedIdeaImplementationPromptTemplate);
		Assert.NotNull(saved.UpdatedAt);
	}

	[Fact]
	public async Task GetSettingsAsync_CreatesDefaultSettingsWithCommitAttributionEnabled()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var settings = await service.GetSettingsAsync();

		Assert.True(settings.EnableCommitAttribution);
	}

	[Fact]
	public async Task GetSettingsAsync_CreatesDefaultIdeaPromptTemplates()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var settings = await service.GetSettingsAsync();

		Assert.Equal(PromptBuilder.DefaultIdeaExpansionPromptTemplate, settings.IdeaExpansionPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultIdeaImplementationPromptTemplate, settings.IdeaImplementationPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate, settings.ApprovedIdeaImplementationPromptTemplate);
	}

	[Fact]
	public async Task GetSettingsAsync_CreatesDefaultSettingsWithUtcTimezoneAndCriticalErrorDefaults()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var settings = await service.GetSettingsAsync();

		Assert.Equal("UTC", settings.TimeZoneId);
		Assert.Equal(AppSettings.DefaultCriticalErrorLogRetentionDays, settings.CriticalErrorLogRetentionDays);
		Assert.Equal(AppSettings.DefaultCriticalErrorLogMaxEntries, settings.CriticalErrorLogMaxEntries);
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

	[Fact]
	public async Task UpdateSettingsAsync_BlankPromptTemplates_ResetToDefaults()
	{
		await using var dbContext = CreateDbContext();
		var service = new SettingsService(dbContext);

		var updated = await service.UpdateSettingsAsync(new AppSettings
		{
			TimeZoneId = "UTC",
			IdeaExpansionPromptTemplate = "   ",
			IdeaImplementationPromptTemplate = "",
			ApprovedIdeaImplementationPromptTemplate = null
		});

		Assert.Equal(PromptBuilder.DefaultIdeaExpansionPromptTemplate, updated.IdeaExpansionPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultIdeaImplementationPromptTemplate, updated.IdeaImplementationPromptTemplate);
		Assert.Equal(PromptBuilder.DefaultApprovedIdeaImplementationPromptTemplate, updated.ApprovedIdeaImplementationPromptTemplate);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	public void Dispose()
	{
		_connection.Dispose();
	}
}
