using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// A job that fails on one provider has to continue on the next one in its plan. The plan
/// and the index were always stored; until now nothing walked them, so a provider running
/// out of quota ended the job instead of moving it.
/// </summary>
public sealed class JobProviderFailoverTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;

	public JobProviderFailoverTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		_rootProvider = services.BuildServiceProvider();

		using var scope = _rootProvider.CreateScope();
		scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Database.EnsureCreated();
	}

	[Fact]
	public async Task FailedJob_MovesToTheNextProviderAndIsQueuedAgain()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var (job, first, second) = await SeedJobWithTwoProviderPlanAsync(dbContext);

		var jobService = new JobService(dbContext, scope.ServiceProvider);
		var movedOn = await jobService.TryFailOverToNextExecutionTargetAsync(job.Id, "Claude usage limit reached");

		Assert.True(movedOn);

		var refreshed = await dbContext.Jobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
		Assert.Equal(second.Id, refreshed.ProviderId);
		Assert.Equal(1, refreshed.ActiveExecutionIndex);
		Assert.NotNull(refreshed.ExecutionPlan);
		Assert.NotEqual(JobStatus.Failed, refreshed.Status);
		Assert.NotNull(refreshed.LastSwitchAt);
		Assert.Contains(first.Name, refreshed.LastSwitchReason);
		Assert.Contains(second.Name, refreshed.LastSwitchReason);
	}

	[Fact]
	public async Task WithNoProvidersLeft_TheJobReallyHasFailed()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var (job, _, _) = await SeedJobWithTwoProviderPlanAsync(dbContext);

		var jobService = new JobService(dbContext, scope.ServiceProvider);

		Assert.True(await jobService.TryFailOverToNextExecutionTargetAsync(job.Id, "first failure"));
		// The plan holds two providers, so the second failure has nowhere left to go.
		Assert.False(await jobService.TryFailOverToNextExecutionTargetAsync(job.Id, "second failure"));
	}

	[Fact]
	public async Task CancelledJob_IsNotHandedToAnotherProvider()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var (job, _, _) = await SeedJobWithTwoProviderPlanAsync(dbContext);

		job.CancellationRequested = true;
		await dbContext.SaveChangesAsync();

		var jobService = new JobService(dbContext, scope.ServiceProvider);

		Assert.False(await jobService.TryFailOverToNextExecutionTargetAsync(job.Id, "stopped by the user"));
	}

	[Fact]
	public async Task DisabledProvider_IsSkipped()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var (job, _, second) = await SeedJobWithTwoProviderPlanAsync(dbContext);

		second.IsEnabled = false;
		await dbContext.SaveChangesAsync();

		var jobService = new JobService(dbContext, scope.ServiceProvider);

		Assert.False(await jobService.TryFailOverToNextExecutionTargetAsync(job.Id, "first failure"));
	}

	private static async Task<(Job Job, Provider First, Provider Second)> SeedJobWithTwoProviderPlanAsync(VibeSwarmDbContext dbContext)
	{
		var first = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude Code",
			Type = ProviderType.Claude,
			IsEnabled = true,
			IsDefault = true
		};
		var second = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			IsEnabled = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "TradeNugget",
			WorkingPath = "/tmp/tradenugget"
		};
		var plan = new List<JobExecutionTarget>
		{
			new() { ProviderId = first.Id, ProviderName = first.Name, Order = 0, Source = "project" },
			new() { ProviderId = second.Id, ProviderName = second.Name, Order = 1, Source = "project" }
		};
		var job = new Job
		{
			Id = Guid.NewGuid(),
			ProjectId = project.Id,
			ProviderId = first.Id,
			GoalPrompt = "Implement the queued idea",
			Status = JobStatus.Processing,
			ExecutionPlan = JsonSerializer.Serialize(plan),
			ActiveExecutionIndex = 0
		};

		dbContext.Providers.AddRange(first, second);
		dbContext.Projects.Add(project);
		dbContext.Jobs.Add(job);
		await dbContext.SaveChangesAsync();

		return (job, first, second);
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}
}
