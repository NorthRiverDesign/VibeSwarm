using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class InferenceProviderServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public InferenceProviderServiceTests()
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
	public async Task CreateAsync_AddsTheNewProviderLast_SoTheDefaultStays()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateService(dbContext);

		await service.CreateAsync(CreateProvider("Zed Ollama", InferenceProviderType.Ollama));
		await service.CreateAsync(CreateProvider("Alpha Grok", InferenceProviderType.Grok));

		var names = (await service.GetEnabledAsync()).Select(provider => provider.Name);
		Assert.Equal(["Zed Ollama", "Alpha Grok"], names);
	}

	[Fact]
	public async Task ReorderAsync_SavesTheOrderAndPicksTheDefaultModelFromTheFirstProvider()
	{
		await using var dbContext = CreateDbContext();
		var service = CreateService(dbContext);
		var ollama = await service.CreateAsync(CreateProvider("Desktop", InferenceProviderType.Ollama, "qwen3"));
		var grok = await service.CreateAsync(CreateProvider("Grok (X.AI)", InferenceProviderType.Grok, "grok-4"));
		Assert.Equal("qwen3", (await service.GetModelForTaskAsync("suggest"))?.ModelId);

		await service.ReorderAsync([grok.Id]);

		await using var readContext = CreateDbContext();
		var readService = CreateService(readContext);
		Assert.Equal([grok.Id, ollama.Id], (await readService.GetAllAsync()).Select(provider => provider.Id));
		Assert.Equal("grok-4", (await readService.GetModelForTaskAsync("suggest"))?.ModelId);
	}

	private static InferenceProvider CreateProvider(string name, InferenceProviderType type, string? defaultModelId = null)
	{
		var provider = new InferenceProvider
		{
			Name = name,
			ProviderType = type,
			Endpoint = "http://localhost:11434",
			IsEnabled = true
		};
		if (defaultModelId != null)
		{
			provider.Models.Add(new InferenceModel
			{
				ModelId = defaultModelId,
				TaskType = "default",
				IsDefault = true,
				IsAvailable = true
			});
		}
		return provider;
	}

	private static InferenceProviderService CreateService(VibeSwarmDbContext dbContext)
		=> new(dbContext, new ServiceCollection().BuildServiceProvider());

	private VibeSwarmDbContext CreateDbContext()
	{
		return new VibeSwarmDbContext(_dbOptions);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}
}
