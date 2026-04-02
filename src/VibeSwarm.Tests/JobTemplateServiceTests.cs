using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class JobTemplateServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public JobTemplateServiceTests()
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
	public async Task CreateAsync_PersistsNormalizedTemplateAndSupportsUsageTracking()
	{
		await using var dbContext = CreateDbContext();
		var provider = CreateProvider();
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(new ProviderModel
		{
			Id = Guid.NewGuid(),
			ProviderId = provider.Id,
			ModelId = "gpt-5.4",
			DisplayName = "GPT-5.4",
			IsAvailable = true,
			IsDefault = true,
			UpdatedAt = DateTime.UtcNow
		});
		await dbContext.SaveChangesAsync();

		var service = new JobTemplateService(dbContext);
		var created = await service.CreateAsync(new JobTemplate
		{
			Name = "  Bug fixer  ",
			Description = "  Reusable bug fix workflow  ",
			GoalPrompt = "  Investigate and fix the reported issue.  ",
			ProviderId = provider.Id,
			ModelId = "gpt-5.4",
			ReasoningEffort = "HIGH",
			Branch = " feature/bugfix ",
			GitChangeDeliveryMode = GitChangeDeliveryMode.PullRequest,
			TargetBranch = " main ",
			CycleMode = CycleMode.FixedCount,
			CycleSessionMode = CycleSessionMode.FreshSession,
			MaxCycles = 3,
			CycleReviewPrompt = "  Re-check tests after each pass. "
		});

		Assert.Equal("Bug fixer", created.Name);
		Assert.Equal("Reusable bug fix workflow", created.Description);
		Assert.Equal("Investigate and fix the reported issue.", created.GoalPrompt);
		Assert.Equal("high", created.ReasoningEffort);
		Assert.Equal("feature/bugfix", created.Branch);
		Assert.Equal("main", created.TargetBranch);
		Assert.Equal(0, created.UseCount);

		var incremented = await service.IncrementUseCountAsync(created.Id);
		Assert.Equal(1, incremented.UseCount);
	}

	[Fact]
	public async Task CreateAsync_RejectsModelWithoutProvider()
	{
		await using var dbContext = CreateDbContext();
		var service = new JobTemplateService(dbContext);

		var error = await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(new JobTemplate
		{
			Name = "Model without provider",
			GoalPrompt = "Run work",
			ModelId = "gpt-5.4"
		}));

		Assert.Equal("Selecting a model requires selecting a provider.", error.Message);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static Provider CreateProvider()
	{
		return new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};
	}

	public void Dispose()
	{
		_connection.Dispose();
	}
}
