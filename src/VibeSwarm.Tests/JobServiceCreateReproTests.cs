using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Tries to reproduce the user-reported tracking error during CreateAsync.
/// Uses sensitive-data logging so any tracking collision reports the offending key.
/// </summary>
public sealed class JobServiceCreateReproTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;

	public JobServiceCreateReproTests()
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
	public async Task CreateAsync_PostedJobWithNullNavProps_SucceedsAfterValidationLoadedTrackedData()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, scope.ServiceProvider);

		// Simulate the controller deserialising a JSON body into a fresh Job POCO.
		// Id is Guid.Empty, nav props null, collections empty — same as FromBody yields.
		var incomingJob = new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Do the thing."
		};

		var created = await service.CreateAsync(incomingJob);

		Assert.NotEqual(Guid.Empty, created.Id);
	}

	[Fact]
	public async Task CreateAsync_WhenAgentPresetSelected_DoesNotCollideWithTrackedAgent()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		var agent = new Agent
		{
			Id = Guid.NewGuid(),
			Name = "Security Reviewer",
			IsEnabled = true,
			DefaultProviderId = provider.Id,
			DefaultModelId = "gpt-5",
			DefaultReasoningEffort = "high"
		};
		dbContext.Agents.Add(agent);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, scope.ServiceProvider);

		var incomingJob = new Job
		{
			ProjectId = project.Id,
			AgentId = agent.Id,
			GoalPrompt = "Review the auth changes."
		};

		var created = await service.CreateAsync(incomingJob);
		Assert.NotEqual(Guid.Empty, created.Id);
	}

	[Fact]
	public async Task CreateAsync_CalledTwice_InSameScope_DoesNotThrowDuplicateTracking()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, scope.ServiceProvider);

		var first = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "First."
		});

		var second = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Second."
		});

		Assert.NotEqual(first.Id, second.Id);
	}

	[Fact]
	public async Task CreateAsync_JobWithAttachedFilesJson_DoesNotCollide()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = SeedProvider(dbContext);
		var project = SeedProject(dbContext, provider.Id);
		await dbContext.SaveChangesAsync();

		var service = new JobService(dbContext, scope.ServiceProvider);

		var incomingJob = new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Job with attachments.",
			AttachedFilesJson = "[\"/tmp/file.txt\"]"
		};

		var created = await service.CreateAsync(incomingJob);
		Assert.NotEqual(Guid.Empty, created.Id);
	}

	private static Provider SeedProvider(VibeSwarmDbContext dbContext)
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		dbContext.Providers.Add(provider);
		dbContext.ProviderModels.Add(new ProviderModel
		{
			ProviderId = provider.Id,
			ModelId = "gpt-5",
			IsAvailable = true,
			IsDefault = true
		});
		return provider;
	}

	private static Project SeedProject(VibeSwarmDbContext dbContext, Guid providerId)
	{
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			ProviderSelections =
			[
				new ProjectProvider { ProviderId = providerId, Priority = 1, IsEnabled = true }
			]
		};
		dbContext.Projects.Add(project);
		return project;
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}
}
