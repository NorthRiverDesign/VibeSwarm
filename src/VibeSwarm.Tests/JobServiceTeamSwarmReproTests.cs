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

	[Fact]
	public async Task CreateAsync_WithTeamSwarmEnabled_NamesEachRolesOwnProfile()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var copilot = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true, IsDefault = true };
		var claude = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
		dbContext.Providers.AddRange(copilot, claude);
		dbContext.ProviderModels.AddRange(
			new ProviderModel { ProviderId = copilot.Id, ModelId = "gpt-5", IsAvailable = true, IsDefault = true },
			new ProviderModel { ProviderId = claude.Id, ModelId = "claude-sonnet", IsAvailable = true, IsDefault = true });
		var builder = new Profile { Id = Guid.NewGuid(), Name = "Builder", ProviderId = copilot.Id, ModelId = "gpt-5" };
		var checker = new Profile { Id = Guid.NewGuid(), Name = "Checker", ProviderId = claude.Id, ModelId = "claude-sonnet" };
		var asked = new Profile { Id = Guid.NewGuid(), Name = "Asked for", ProviderId = claude.Id, ModelId = "claude-sonnet" };
		dbContext.Profiles.AddRange(builder, checker, asked);

		var agent1 = new Agent { Id = Guid.NewGuid(), Name = "A", IsEnabled = true, DefaultProviderId = copilot.Id };
		var agent2 = new Agent { Id = Guid.NewGuid(), Name = "B", IsEnabled = true, DefaultProviderId = claude.Id };
		dbContext.Agents.AddRange(agent1, agent2);
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "Profiled swarm",
			WorkingPath = "/tmp/profiled-swarm",
			EnableTeamSwarm = true,
			AgentAssignments =
			[
				new ProjectAgent { AgentId = agent1.Id, ProviderId = copilot.Id, PreferredModelId = "gpt-5", ProfileId = builder.Id, IsEnabled = true },
				new ProjectAgent { AgentId = agent2.Id, ProviderId = claude.Id, PreferredModelId = "claude-sonnet", ProfileId = checker.Id, IsEnabled = true }
			]
		};
		dbContext.Projects.Add(project);
		await dbContext.SaveChangesAsync();

		// The job is asked for on one profile, but a team swarm hands each role its own.
		await new JobService(dbContext, scope.ServiceProvider).CreateAsync(new Job { ProjectId = project.Id, ProfileId = asked.Id, GoalPrompt = "Swarm this." });

		var jobs = await dbContext.Jobs.AsNoTracking().ToListAsync();
		Assert.Equal(
			[("Builder", copilot.Id), ("Checker", claude.Id)],
			jobs.Select(job => (job.ProfileName!, job.ProviderId)).OrderBy(pair => pair.Item1));
	}

	[Fact]
	public async Task CreateAsync_LocalEnvironmentSetupJob_RunsAloneEvenWithTeamSwarmEnabled()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider1 = new Provider { Id = Guid.NewGuid(), Name = "Copilot", Type = ProviderType.Copilot, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true, IsDefault = true };
		var provider2 = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
		dbContext.Providers.AddRange(provider1, provider2);
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

		var created = await service.CreateAsync(LocalEnvironmentSetup.CreateJob(project.Id, provider1.Id, null, null));

		var stored = Assert.Single(await dbContext.Jobs.AsNoTracking().ToListAsync());
		Assert.Equal(created.Id, stored.Id);
		Assert.Null(stored.SwarmId);
		Assert.True(LocalEnvironmentSetup.IsSetupJob(stored));
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}
}
