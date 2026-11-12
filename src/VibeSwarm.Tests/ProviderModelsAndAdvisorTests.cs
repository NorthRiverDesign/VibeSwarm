using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProviderModelsAndAdvisorTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public ProviderModelsAndAdvisorTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = new VibeSwarmDbContext(_dbOptions);
		dbContext.Database.EnsureCreated();
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task RefreshModelsAsync_AddsNewModelsWithoutTakingTheChosenDefault()
	{
		var provider = ClaudeApiProvider();
		await SeedAsync(provider, new ProviderModel { ProviderId = provider.Id, ModelId = "claude-opus-5-5", IsDefault = true });

		await using (var dbContext = new VibeSwarmDbContext(_dbOptions))
		{
			await new ProviderService(dbContext).RefreshModelsAsync(provider.Id);
		}

		await using var verification = new VibeSwarmDbContext(_dbOptions);
		var models = await verification.ProviderModels.Where(m => m.ProviderId == provider.Id).ToListAsync();
		Assert.Contains(models, m => m.ModelId == "claude-fable-5-2" && m.IsAvailable);
		Assert.Equal(["claude-opus-5-5"], models.Where(m => m.IsDefault).Select(m => m.ModelId));
	}

	[Fact]
	public async Task RefreshModelsAsync_PicksOneDefaultOnFirstRefresh()
	{
		var provider = ClaudeApiProvider();
		await SeedAsync(provider);

		await using (var dbContext = new VibeSwarmDbContext(_dbOptions))
		{
			await new ProviderService(dbContext).RefreshModelsAsync(provider.Id);
		}

		await using var verification = new VibeSwarmDbContext(_dbOptions);
		Assert.Equal(1, await verification.ProviderModels.CountAsync(m => m.ProviderId == provider.Id && m.IsDefault));
	}

	[Theory]
	[InlineData(ProviderType.Claude, ProviderConnectionMode.CLI, " fable ", "fable")]
	[InlineData(ProviderType.Claude, ProviderConnectionMode.CLI, "OFF", "off")]
	[InlineData(ProviderType.Claude, ProviderConnectionMode.CLI, "  ", null)]
	[InlineData(ProviderType.Claude, ProviderConnectionMode.SDK, "opus", null)]
	[InlineData(ProviderType.Copilot, ProviderConnectionMode.CLI, "opus", null)]
	public async Task UpdateAsync_SavesTheAdvisorOnlyForClaudeCode(ProviderType type, ProviderConnectionMode mode, string advisor, string? expected)
	{
		var provider = new Provider { Name = "Agent", Type = type, ConnectionMode = mode, ApiKey = "sk-test" };
		await SeedAsync(provider);

		await using (var dbContext = new VibeSwarmDbContext(_dbOptions))
		{
			await new ProviderService(dbContext).UpdateAsync(new Provider
			{
				Id = provider.Id,
				Name = provider.Name,
				Type = type,
				ConnectionMode = mode,
				ApiKey = provider.ApiKey,
				AdvisorModel = advisor
			});
		}

		await using var verification = new VibeSwarmDbContext(_dbOptions);
		Assert.Equal(expected, (await verification.Providers.SingleAsync(p => p.Id == provider.Id)).AdvisorModel);
	}

	private static Provider ClaudeApiProvider() => new()
	{
		Name = "Claude API",
		Type = ProviderType.Claude,
		ConnectionMode = ProviderConnectionMode.SDK,
		ApiKey = "sk-test"
	};

	private async Task SeedAsync(Provider provider, params ProviderModel[] models)
	{
		await using var dbContext = new VibeSwarmDbContext(_dbOptions);
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.AddRange(models);
		await dbContext.SaveChangesAsync();
	}
}
