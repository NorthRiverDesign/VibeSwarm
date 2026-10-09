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

	[Fact]
	public async Task ApplyDetectedLimitsAsync_RecordsWhenLimitsWereReadLive()
	{
		await using var dbContext = CreateDbContext();
		var providerId = await AddClaudeProviderAsync(dbContext);
		var service = CreateService(dbContext);
		var before = DateTime.UtcNow;

		var summary = await service.ApplyDetectedLimitsAsync(providerId, UsageLimitWindowHelper.CreateUsageLimits(
			UsageLimitType.SessionLimit,
			null,
			[SessionWindow(40, DateTime.UtcNow.AddHours(2))]));

		Assert.NotNull(summary.LimitsRefreshedAt);
		Assert.True(summary.LimitsRefreshedAt >= before);
	}

	[Fact]
	public async Task CheckExhaustionAsync_IgnoresALimitWhoseWindowHasReset()
	{
		await using var dbContext = CreateDbContext();
		var providerId = await AddClaudeProviderAsync(dbContext);
		dbContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
		{
			ProviderId = providerId,
			LimitType = UsageLimitType.SessionLimit,
			CurrentUsage = 100,
			MaxUsage = 100,
			IsLimitReached = true,
			LimitResetTime = DateTime.UtcNow.AddMinutes(-5)
		});
		await dbContext.SaveChangesAsync();

		var warning = await CreateService(dbContext).CheckExhaustionAsync(providerId);

		Assert.Null(warning);
	}

	[Fact]
	public async Task CheckExhaustionAsync_KeepsAReachedWeeklyLimitAfterTheSessionResets()
	{
		await using var dbContext = CreateDbContext();
		var providerId = await AddClaudeProviderAsync(dbContext);
		var service = CreateService(dbContext);
		await service.ApplyDetectedLimitsAsync(providerId, UsageLimitWindowHelper.CreateUsageLimits(
			UsageLimitType.RateLimit,
			null,
			[
				// Both reached: the session outranks the weekly window, so it sets the summary's reset time.
				new UsageLimitWindow
				{
					Scope = UsageLimitWindowScope.Session,
					LimitType = UsageLimitType.SessionLimit,
					CurrentUsage = 100,
					MaxUsage = 100,
					IsLimitReached = true,
					ResetTime = DateTime.UtcNow.AddMinutes(-5)
				},
				new UsageLimitWindow
				{
					Scope = UsageLimitWindowScope.Weekly,
					LimitType = UsageLimitType.RateLimit,
					CurrentUsage = 100,
					MaxUsage = 100,
					IsLimitReached = true,
					ResetTime = DateTime.UtcNow.AddDays(2)
				}
			]));

		var summary = await service.GetUsageSummaryAsync(providerId);
		var warning = await service.CheckExhaustionAsync(providerId);

		Assert.True(summary!.LimitResetTime < DateTime.UtcNow);
		Assert.NotNull(warning);
		Assert.True(warning!.IsExhausted);
	}

	[Fact]
	public async Task SessionLimitGuard_HoldsWhenRecentJobsSayTheNextOneWouldReachTheLimit()
	{
		await using var dbContext = CreateDbContext();
		var providerId = await AddClaudeProviderAsync(dbContext);
		var sessionReset = DateTime.UtcNow.AddHours(3);
		var project = new Project { Name = "Session project", WorkingPath = "/tmp/session-project" };
		dbContext.Projects.Add(project);

		var recordedAt = DateTime.UtcNow.AddHours(-1);
		foreach (var sessionPercent in new[] { 10, 40, 70 })
		{
			var job = new Job { ProjectId = project.Id, ProviderId = providerId, GoalPrompt = "Work", Status = JobStatus.Completed };
			dbContext.Jobs.Add(job);
			dbContext.ProviderUsageRecords.Add(new ProviderUsageRecord
			{
				ProviderId = providerId,
				JobId = job.Id,
				RecordedAt = recordedAt,
				DetectedLimitWindows = [SessionWindow(sessionPercent, sessionReset)]
			});
			recordedAt = recordedAt.AddMinutes(20);
		}

		dbContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
		{
			ProviderId = providerId,
			LimitWindows = [SessionWindow(70, sessionReset)]
		});
		await dbContext.SaveChangesAsync();

		var hold = await ProviderSessionLimitGuard.GetHoldAsync(dbContext, providerId, DateTime.UtcNow, CancellationToken.None);

		Assert.NotNull(hold);
		Assert.Equal(sessionReset, hold!.Until);
		Assert.Equal(30, hold.ExpectedJobUsagePercent);
		Assert.True(hold.NextJobWouldReachLimit);
	}

	[Fact]
	public async Task SessionLimitGuard_UsesTheProvidersConfiguredThreshold()
	{
		await using var dbContext = CreateDbContext();
		var providerId = await AddClaudeProviderAsync(dbContext, pauseThresholdPercent: 75);
		dbContext.ProviderUsageSummaries.Add(new ProviderUsageSummary
		{
			ProviderId = providerId,
			LimitWindows = [SessionWindow(80, DateTime.UtcNow.AddHours(1))]
		});
		await dbContext.SaveChangesAsync();

		var hold = await ProviderSessionLimitGuard.GetHoldAsync(dbContext, providerId, DateTime.UtcNow, CancellationToken.None);

		Assert.NotNull(hold);
		Assert.Equal(75, hold!.PauseThresholdPercent);
	}

	private static async Task<Guid> AddClaudeProviderAsync(VibeSwarmDbContext dbContext, int? pauseThresholdPercent = null)
	{
		var providerId = Guid.NewGuid();
		dbContext.Providers.Add(new Provider
		{
			Id = providerId,
			Name = "Claude",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			SessionLimitPauseThresholdPercent = pauseThresholdPercent
		});
		await dbContext.SaveChangesAsync();
		return providerId;
	}

	private static UsageLimitWindow SessionWindow(int percentUsed, DateTime resetTime) => new()
	{
		Scope = UsageLimitWindowScope.Session,
		LimitType = UsageLimitType.SessionLimit,
		Label = "Session (5 hours)",
		CurrentUsage = percentUsed,
		MaxUsage = 100,
		ResetTime = resetTime
	};

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
