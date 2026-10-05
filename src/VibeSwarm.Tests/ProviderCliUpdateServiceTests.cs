using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ProviderCliUpdateServiceTests : IDisposable
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private readonly RecordingProviderService _providers = new();
	private readonly ServiceProvider _services;

	public ProviderCliUpdateServiceTests()
	{
		_connection.Open();
		var services = new ServiceCollection();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IProviderService>(_providers);
		_services = services.BuildServiceProvider();
		using var scope = _services.CreateScope();
		scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Database.EnsureCreated();
	}

	[Fact]
	public async Task UpdatesEachIdleCliOnceAndSkipsSdkProviders()
	{
		var claude = AddProvider(ProviderType.Claude, ProviderConnectionMode.CLI, "/bin/claude");
		AddProvider(ProviderType.Claude, ProviderConnectionMode.CLI, "/bin/claude");
		var copilot = AddProvider(ProviderType.Copilot, ProviderConnectionMode.CLI, "/bin/copilot");
		AddProvider(ProviderType.Copilot, ProviderConnectionMode.SDK, "/bin/copilot");

		await CreateService().UpdateDueProvidersAsync(DateTime.UtcNow, CancellationToken.None);

		Assert.Equal([claude.Id, copilot.Id], _providers.Updated);
	}

	[Fact]
	public async Task WaitsForRunningJobsAndUpdatesOncePerInterval()
	{
		var claude = AddProvider(ProviderType.Claude, ProviderConnectionMode.CLI, "/bin/claude");
		var job = await AddJobAsync(claude, JobStatus.Processing);
		var service = CreateService();
		var now = DateTime.UtcNow;

		await service.UpdateDueProvidersAsync(now, CancellationToken.None);
		Assert.Empty(_providers.Updated);

		await SetStatusAsync(job, JobStatus.Completed);
		await service.UpdateDueProvidersAsync(now.AddMinutes(15), CancellationToken.None);
		await service.UpdateDueProvidersAsync(now.AddMinutes(30), CancellationToken.None);
		Assert.Equal([claude.Id], _providers.Updated);

		await service.UpdateDueProvidersAsync(now.AddMinutes(15) + ProviderCliUpdateService.UpdateInterval, CancellationToken.None);
		Assert.Equal([claude.Id, claude.Id], _providers.Updated);
	}

	private ProviderCliUpdateService CreateService() => new(
		_services.GetRequiredService<IServiceScopeFactory>(),
		new ConfigurationBuilder().Build(),
		NullLogger<ProviderCliUpdateService>.Instance);

	private Provider AddProvider(ProviderType type, ProviderConnectionMode mode, string executablePath)
	{
		var provider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = $"{type} {mode}",
			Type = type,
			ConnectionMode = mode,
			ExecutablePath = executablePath,
			IsEnabled = true
		};
		_providers.All.Add(provider);
		return provider;
	}

	private async Task<Job> AddJobAsync(Provider provider, JobStatus status)
	{
		using var scope = _services.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var project = new Project { Id = Guid.NewGuid(), Name = "Project", WorkingPath = "/tmp" };
		var job = new Job { Id = Guid.NewGuid(), ProjectId = project.Id, ProviderId = provider.Id, GoalPrompt = "x", Status = status };
		dbContext.AddRange(project, provider, job);
		await dbContext.SaveChangesAsync();
		return job;
	}

	private async Task SetStatusAsync(Job job, JobStatus status)
	{
		using var scope = _services.CreateScope();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
		var stored = await dbContext.Jobs.SingleAsync(j => j.Id == job.Id);
		stored.Status = status;
		await dbContext.SaveChangesAsync();
	}

	public void Dispose()
	{
		_services.Dispose();
		_connection.Dispose();
	}

	private sealed class RecordingProviderService : FakeProviderServiceBase
	{
		public List<Provider> All { get; } = [];
		public List<Guid> Updated { get; } = [];

		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult<IEnumerable<Provider>>(All);

		public override Task<CliUpdateResult> UpdateCliAsync(Guid id, CancellationToken cancellationToken = default)
		{
			Updated.Add(id);
			return Task.FromResult(CliUpdateResult.Ok("1.0.0", "1.0.0"));
		}
	}
}
