using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// Exercises the production CreateAsync path where JobService receives a real
/// IServiceProvider that can hand out fresh DbContext scopes — mirroring how
/// ASP.NET Core wires request-scoped services.
/// </summary>
public sealed class JobServiceCreateRealScopeTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _rootProvider;

	public JobServiceCreateRealScopeTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		_rootProvider = services.BuildServiceProvider();

		using var scope = _rootProvider.CreateScope();
		var seed = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		seed.Database.EnsureCreated();
	}

	[Fact]
	public async Task CreateAsync_DoesNotThrowTrackingConflict_WhenProviderAlreadyTouchedInSameScope()
	{
		using var scope = _rootProvider.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsEnabled = true,
			IsDefault = true
		};
		var project = new Project
		{
			Id = Guid.NewGuid(),
			Name = "VibeSwarm",
			WorkingPath = "/tmp/vibeswarm",
			ProviderSelections =
			[
				new ProjectProvider { ProviderId = provider.Id, Priority = 1, IsEnabled = true }
			]
		};
		dbContext.Providers.Add(provider);
		dbContext.Projects.Add(project);
		dbContext.ProviderModels.Add(new ProviderModel
		{
			ProviderId = provider.Id,
			ModelId = "gpt-5",
			IsAvailable = true,
			IsDefault = true
		});
		await dbContext.SaveChangesAsync();

		// Pre-load entities so the scoped _dbContext has tracked state mirroring a real
		// request that already ran other queries before hitting CreateAsync.
		_ = await dbContext.Providers.Where(p => p.IsEnabled).ToListAsync();
		_ = await dbContext.Projects.FirstAsync(p => p.Id == project.Id);

		var service = new JobService(dbContext, scope.ServiceProvider);

		var created = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Ship the feature."
		});

		Assert.NotEqual(Guid.Empty, created.Id);
		Assert.Equal(project.Id, created.ProjectId);
		Assert.Equal(provider.Id, created.ProviderId);

		// Second create in the same scope — this is where a naive implementation would
		// fail because the fresh scope's DbContext is disposed and the original still
		// has tracker entries from the first call.
		var second = await service.CreateAsync(new Job
		{
			ProjectId = project.Id,
			ProviderId = provider.Id,
			GoalPrompt = "Follow-up."
		});

		Assert.NotEqual(created.Id, second.Id);
	}

	public void Dispose()
	{
		_rootProvider.Dispose();
		_connection.Dispose();
	}
}
