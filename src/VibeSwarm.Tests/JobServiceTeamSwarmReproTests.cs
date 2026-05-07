using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Exercises the team swarm path where CreateAsync fans out to sibling jobs.
/// Validates that the shared DbContext still works correctly after the primary
/// job is persisted in an isolated scope.
/// </summary>
public sealed class JobServiceTeamSwarmReproTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;

	public JobServiceTeamSwarmReproTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options =>
		{
			options.UseSqlite(_connection);
			options.EnableSensitiveDataLogging();
			options.EnableDetailedErrors();
		});
		_rootProvider = services.BuildServiceProvider();

		using var scope = _rootProvider.CreateScope();
		var seed = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		seed.Database.EnsureCreated();
	}

	[Fact]
	public async Task CreateAsync_WithTeamSwarmEnabled_FansOutSiblingsWithoutTrackingError()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider1 = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true, IsDefault = true };
		var provider2 = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
		dbContext.Providers.AddRange(provider1, provider2);
		dbContext.ProviderModels.AddRange(
			new ProviderModel { ProviderId = provider1.Id, ModelId = "gpt-5", IsAvailable = true, IsDefault = true },
			new ProviderModel { ProviderId = provider2.Id, ModelId = "claude-sonnet", IsAvailable = true, IsDefault = true });

		var agent1 = new Agent { Id = Guid.NewGuid(), Name = "A", IsEnabled = true, DefaultProviderId = provider1.Id };
		var agent2 = new Agent { Id = Guid.NewGuid(), Name = "B", IsEnabled = true, DefaultProviderId = provider2.Id };
		dbContext.Agents.AddRange(agent1, agent2);

		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			EnableTeamSwarm = true,
			AgentAssignments =
			[
				new ProjectAgent { AgentId = agent1.Id, ProviderId = provider1.Id, IsEnabled = true },
				new ProjectAgent { AgentId = agent2.Id, ProviderId = provider2.Id, IsEnabled = true }
			]
		};
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, scope.ServiceProvider);

		var created = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider1.Id,
			GoalPrompt = "Swarm this."
		});

		Assert.NotEqual(Guid.Empty, created.Id);
		var allJobs = await dbContext.Jobs.AsNoTracking().ToListAsync();
		Assert.Equal(2, allJobs.Count);
		Assert.All(allJobs, j => Assert.Equal(created.SwarmId, j.SwarmId));
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}
}
