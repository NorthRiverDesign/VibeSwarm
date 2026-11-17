using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProviderUsageLimitsTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public ProviderUsageLimitsTests()
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
	public async Task UpdateUsageLimitsAsync_ChangesOnlyTheLimits()
	{
		var providerId = await SeedProviderAsync(new Provider
		{
			Name = "Claude Code",
			Type = ProviderType.Claude,
			ApiKey = "sk-keep-me",
			MaxExecutionMinutes = 600,
			SessionLimitPauseThresholdPercent = 80
		});

		await using (var dbContext = new VibeSwarmDbContext(_dbOptions))
		{
			await new ProviderService(dbContext).UpdateUsageLimitsAsync(providerId, new ProviderUsageLimits(95, 75, null));
		}

		await using var verification = new VibeSwarmDbContext(_dbOptions);
		var provider = await verification.Providers.SingleAsync(p => p.Id == providerId);
		Assert.Equal(95, provider.SessionLimitPauseThresholdPercent);
		Assert.Equal(75, provider.WeeklyLimitPauseThresholdPercent);
		Assert.Equal("sk-keep-me", provider.ApiKey);
		Assert.Equal(600, provider.MaxExecutionMinutes);
	}

	[Theory]
	[InlineData(90, null)]
	[InlineData(20, 50)]
	[InlineData(150, 100)]
	[InlineData(null, null)]
	public async Task UpdateUsageLimitsAsync_ClampsThresholds_AndStoresTheDefaultAsUnset(int? requested, int? stored)
	{
		var providerId = await SeedProviderAsync(new Provider { Name = "Claude Code", Type = ProviderType.Claude });

		await using var dbContext = new VibeSwarmDbContext(_dbOptions);
		var provider = await new ProviderService(dbContext).UpdateUsageLimitsAsync(providerId, new ProviderUsageLimits(requested, requested, null));

		Assert.Equal(stored, provider.SessionLimitPauseThresholdPercent);
		Assert.Equal(stored, provider.WeeklyLimitPauseThresholdPercent);
	}

	[Theory]
	[InlineData(300, 300)]
	[InlineData(0, null)]
	[InlineData(null, null)]
	public async Task UpdateUsageLimitsAsync_SetsOrClearsTheMonthlyRequestLimit(int? requested, int? stored)
	{
		var providerId = await SeedProviderAsync(new Provider { Name = "Copilot", Type = ProviderType.Copilot, ConfiguredUsageLimit = 1000 });

		await using var dbContext = new VibeSwarmDbContext(_dbOptions);
		var provider = await new ProviderService(dbContext).UpdateUsageLimitsAsync(providerId, new ProviderUsageLimits(null, null, requested));

		Assert.Equal(stored, provider.ConfiguredUsageLimit);
	}

	[Fact]
	public async Task GetHoldAsync_HoldsJobsAtTheProvidersWeeklyThreshold()
	{
		var providerId = await SeedProviderAsync(new Provider { Name = "Claude Code", Type = ProviderType.Claude, WeeklyLimitPauseThresholdPercent = 80 });
		var now = DateTime.UtcNow;
		var weeklyReset = now.AddDays(2);
		await using (var dbContext = new VibeSwarmDbContext(_dbOptions))
		{
			dbContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
			{
				ProviderId = providerId,
				LimitType = UsageLimitType.RateLimit,
				LimitWindows =
				[
					new UsageLimitWindow { Scope = UsageLimitWindowScope.Session, CurrentUsage = 5, MaxUsage = 100, ResetTime = now.AddHours(3) },
					new UsageLimitWindow { Scope = UsageLimitWindowScope.Weekly, CurrentUsage = 82, MaxUsage = 100, ResetTime = weeklyReset }
				]
			});
			await dbContext.SaveChangesAsync();
		}

		await using var guardContext = new VibeSwarmDbContext(_dbOptions);
		var hold = await ProviderSessionLimitGuard.GetHoldAsync(guardContext, providerId, now, CancellationToken.None);

		Assert.NotNull(hold);
		Assert.Equal(weeklyReset, hold!.Until, TimeSpan.FromSeconds(1));
		Assert.Equal(UsageLimitWindowScope.Weekly, hold.Scope);
	}

	private async Task<Guid> SeedProviderAsync(Provider provider)
	{
		await using var dbContext = new VibeSwarmDbContext(_dbOptions);
		dbContext.Providers.Add(provider);
		await dbContext.SaveChangesAsync();
		return provider.Id;
	}
}
