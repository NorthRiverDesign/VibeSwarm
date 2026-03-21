using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class CommonProviderSetupServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;
	private readonly string _tempDirectory;

	public CommonProviderSetupServiceTests()
	{
		_tempDirectory = Path.Combine(Path.GetTempPath(), "vibeswarm-common-provider-setup-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDirectory);

		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task GetStatusesAsync_UsesConfiguredConnectionModeForAuthenticationStatus()
	{
		await using var dbContext = CreateDbContext();
		var cliProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude CLI",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsDefault = false,
			IsEnabled = true
		};
		var sdkProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude SDK",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.SDK,
			ApiKey = "sk-ant-test",
			IsDefault = true,
			IsEnabled = true
		};

		dbContext.Providers.AddRange(cliProvider, sdkProvider);
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext);

		var statuses = await service.GetStatusesAsync();
		var status = Assert.Single(statuses, item => item.ProviderType == ProviderType.Claude);

		Assert.True(status.HasConfiguredProvider);
		Assert.Equal(sdkProvider.Id, status.ProviderId);
		Assert.Equal(sdkProvider.Name, status.ProviderName);
		Assert.Equal(ProviderConnectionMode.SDK, status.AuthenticationConnectionMode);
		Assert.True(status.IsAuthenticated);
		Assert.Equal("Saved in VibeSwarm for this SDK connection.", status.AuthenticationStatus);
	}

	[Fact]
	public async Task GetStatusesAsync_DetectsCopilotCliLoginForConfiguredSdkConnection()
	{
		await using var dbContext = CreateDbContext();
		dbContext.Providers.Add(new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot SDK",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.SDK,
			IsDefault = true,
			IsEnabled = true
		});
		await dbContext.SaveChangesAsync();

		var homeDirectory = Path.Combine(_tempDirectory, "home");
		var copilotDirectory = Path.Combine(homeDirectory, ".copilot");
		Directory.CreateDirectory(copilotDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(copilotDirectory, "config.json"),
			"""
			{
			  "last_logged_in_user": {
			    "login": "octocat"
			  }
			}
			""");

		using var homeScope = new EnvironmentVariableScope("HOME", homeDirectory);
		using var copilotTokenScope = new EnvironmentVariableScope("COPILOT_GITHUB_TOKEN", null);
		using var ghTokenScope = new EnvironmentVariableScope("GH_TOKEN", null);
		using var githubTokenScope = new EnvironmentVariableScope("GITHUB_TOKEN", null);

		var service = CreateService(dbContext);

		var statuses = await service.GetStatusesAsync();
		var status = Assert.Single(statuses, item => item.ProviderType == ProviderType.Copilot);

		Assert.Equal(ProviderConnectionMode.SDK, status.AuthenticationConnectionMode);
		Assert.True(status.IsAuthenticated);
		Assert.Equal("Copilot CLI login detected on host for this SDK connection.", status.AuthenticationStatus);
	}

	public void Dispose()
	{
		_connection.Dispose();

		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
	}

	private VibeSwarmDbContext CreateDbContext() => new(_dbOptions);

	private static CommonProviderSetupService CreateService(VibeSwarmDbContext dbContext)
	{
		var detectionService = new ProviderCliDetectionService(NullLogger<ProviderCliDetectionService>.Instance);
		var agentDetectionService = new AgentDetectionService(dbContext, NullLogger<AgentDetectionService>.Instance, detectionService);
		return new CommonProviderSetupService(dbContext, detectionService, agentDetectionService);
	}

	private sealed class EnvironmentVariableScope : IDisposable
	{
		private readonly string _name;
		private readonly string? _originalValue;

		public EnvironmentVariableScope(string name, string? value)
		{
			_name = name;
			_originalValue = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public void Dispose()
		{
			Environment.SetEnvironmentVariable(_name, _originalValue);
		}
	}
}
