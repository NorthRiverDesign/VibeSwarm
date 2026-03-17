using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProviderUsageServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public ProviderUsageServiceTests()
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
	public async Task RecordUsageAsync_CopilotConfiguredBudget_TracksCumulativePremiumRequestUsage()
	{
		await using var dbContext = CreateDbContext();
		var providerId = Guid.NewGuid();
		dbContext.Providers.Add(new Provider
		{
			Id = providerId,
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			ConfiguredUsageLimit = 300,
			ConfiguredLimitType = UsageLimitType.PremiumRequests
		});
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext);

		await service.RecordUsageAsync(providerId, null, new ExecutionResult
		{
			PremiumRequestsConsumed = 3
		});

		await service.RecordUsageAsync(providerId, null, new ExecutionResult
		{
			PremiumRequestsConsumed = 2
		});

		var summary = await service.GetUsageSummaryAsync(providerId);

		Assert.NotNull(summary);
		Assert.Equal(UsageLimitType.PremiumRequests, summary!.LimitType);
		Assert.Equal(5, summary.CurrentUsage);
		Assert.Equal(5, summary.TotalPremiumRequestsConsumed);
		Assert.Equal(300, summary.ConfiguredMaxUsage);
		Assert.Equal(300, summary.EffectiveMaxUsage);
		var monthlyWindow = Assert.Single(summary.LimitWindows);
		Assert.Equal(UsageLimitWindowScope.Monthly, monthlyWindow.Scope);
		Assert.Equal(5, monthlyWindow.CurrentUsage);
		Assert.Equal(300, monthlyWindow.MaxUsage);
	}

	[Fact]
	public async Task RecordUsageAsync_ProviderSnapshot_PreservesLatestBudgetValues()
	{
		await using var dbContext = CreateDbContext();
		var providerId = Guid.NewGuid();
		dbContext.Providers.Add(new Provider
		{
			Id = providerId,
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			ConfiguredUsageLimit = 300,
			ConfiguredLimitType = UsageLimitType.PremiumRequests
		});
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext);

		await service.RecordUsageAsync(providerId, null, new ExecutionResult
		{
			PremiumRequestsConsumed = 3,
			DetectedUsageLimits = new UsageLimits
			{
				LimitType = UsageLimitType.PremiumRequests,
				CurrentUsage = 42,
				MaxUsage = 300,
				Message = "Premium requests used: 42/300"
			}
		});

		var summary = await service.GetUsageSummaryAsync(providerId);

		Assert.NotNull(summary);
		Assert.Equal(42, summary!.CurrentUsage);
		Assert.Equal(300, summary.MaxUsage);
		Assert.Equal(3, summary.TotalPremiumRequestsConsumed);
		Assert.Equal("Premium requests used: 42/300", summary.LimitMessage);
		var monthlyWindow = Assert.Single(summary.LimitWindows);
		Assert.Equal(UsageLimitWindowScope.Monthly, monthlyWindow.Scope);
		Assert.Equal(42, monthlyWindow.CurrentUsage);
		Assert.Equal(300, monthlyWindow.MaxUsage);
	}

	[Fact]
	public async Task RecordUsageAsync_ClaudeMultiWindowSnapshot_PersistsDetailedLimitWindows()
	{
		await using var dbContext = CreateDbContext();
		var providerId = Guid.NewGuid();
		dbContext.Providers.Add(new Provider
		{
			Id = providerId,
			Name = "Claude",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI
		});
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext);

		await service.RecordUsageAsync(providerId, null, new ExecutionResult
		{
			DetectedUsageLimits = UsageLimitWindowHelper.CreateUsageLimits(
				UsageLimitType.RateLimit,
				"Weekly limit 72/100 used.",
				[
					new UsageLimitWindow
					{
						Scope = UsageLimitWindowScope.Session,
						LimitType = UsageLimitType.SessionLimit,
						CurrentUsage = 18,
						MaxUsage = 50,
						Message = "Session limit 18/50 used."
					},
					new UsageLimitWindow
					{
						Scope = UsageLimitWindowScope.Weekly,
						LimitType = UsageLimitType.RateLimit,
						CurrentUsage = 72,
						MaxUsage = 100,
						Message = "Weekly limit 72/100 used."
					}
				])
		});

		var summary = await service.GetUsageSummaryAsync(providerId);
		var history = await service.GetUsageHistoryAsync(providerId);

		Assert.NotNull(summary);
		Assert.Equal(2, summary!.LimitWindows.Count);
		Assert.Contains(summary.LimitWindows, window => window.Scope == UsageLimitWindowScope.Session && window.CurrentUsage == 18);
		Assert.Contains(summary.LimitWindows, window => window.Scope == UsageLimitWindowScope.Weekly && window.CurrentUsage == 72);
		Assert.Single(history);
		Assert.Equal(2, history[0].DetectedLimitWindows.Count);
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static ProviderUsageService CreateService(VibeSwarmDbContext dbContext)
	{
		return new ProviderUsageService(dbContext, NullLogger<ProviderUsageService>.Instance);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}
}
