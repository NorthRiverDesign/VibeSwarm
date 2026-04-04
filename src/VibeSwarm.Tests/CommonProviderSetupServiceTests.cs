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
		Assert.Equal("API Key", status.AuthenticationTypeLabel);
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
		Assert.Equal("Logged-in User", status.AuthenticationTypeLabel);
		Assert.True(status.IsAuthenticated);
		Assert.True(
			status.AuthenticationStatus?.Contains("authentication detected", StringComparison.OrdinalIgnoreCase) == true ||
			status.AuthenticationStatus?.Contains("Copilot CLI login detected", StringComparison.OrdinalIgnoreCase) == true);
	}

	[Fact]
	public async Task GetStatusesAsync_ReportsCopilotHostSearchTools()
	{
		await using var dbContext = CreateDbContext();

		var homeDirectory = Path.Combine(_tempDirectory, "tool-home");
		Directory.CreateDirectory(homeDirectory);

		var toolDirectory = OperatingSystem.IsWindows()
			? Path.Combine(_tempDirectory, "bin")
			: Path.Combine(homeDirectory, ".local", "bin");
		Directory.CreateDirectory(toolDirectory);

		if (OperatingSystem.IsWindows())
		{
			await File.WriteAllTextAsync(Path.Combine(toolDirectory, "rg.bat"), """
				@echo off
				echo ripgrep 14.1.1
				""");
			await File.WriteAllTextAsync(Path.Combine(toolDirectory, "fdfind.bat"), """
				@echo off
				echo fdfind 10.2.0
				""");
		}
		else
		{
			var rgPath = Path.Combine(toolDirectory, "rg");
			await File.WriteAllTextAsync(rgPath, """
				#!/bin/sh
				echo "ripgrep 14.1.1"
				""");
			File.SetUnixFileMode(rgPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

			var fdPath = Path.Combine(toolDirectory, "fdfind");
			await File.WriteAllTextAsync(fdPath, """
				#!/bin/sh
				echo "fdfind 10.2.0"
				""");
			File.SetUnixFileMode(fdPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		using var homeScope = new EnvironmentVariableScope("HOME", homeDirectory);
		using var pathScope = new EnvironmentVariableScope("PATH", string.Join(Path.PathSeparator, new[]
		{
			toolDirectory,
			Environment.GetEnvironmentVariable("PATH") ?? string.Empty
		}));

		var service = CreateService(dbContext);
		var statuses = await service.GetStatusesAsync();
		var status = Assert.Single(statuses, item => item.ProviderType == ProviderType.Copilot);

		Assert.Collection(status.HostTools,
			ripgrep =>
			{
				Assert.Equal("ripgrep", ripgrep.Name);
				Assert.True(ripgrep.IsInstalled);
				Assert.NotNull(ripgrep.ResolvedExecutablePath);
			},
			fd =>
			{
				Assert.Equal("fd", fd.Name);
				Assert.True(fd.IsInstalled);
				Assert.NotNull(fd.ResolvedExecutablePath);
				Assert.True(
					fd.ResolvedExecutablePath!.Contains("fd", StringComparison.OrdinalIgnoreCase) ||
					fd.ResolvedExecutablePath.Contains("fdfind", StringComparison.OrdinalIgnoreCase));
			});
	}

	[Fact]
	public async Task SaveAuthenticationAsync_UpdatesDefaultClaudeSdkProvider()
	{
		await using var dbContext = CreateDbContext();
		var sdkProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude SDK",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.SDK,
			IsDefault = true,
			IsEnabled = true
		};
		var cliProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude CLI",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.CLI,
			IsDefault = false,
			IsEnabled = true
		};

		dbContext.Providers.AddRange(sdkProvider, cliProvider);
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext);
		var result = await service.SaveAuthenticationAsync(new CommonProviderSetupRequest
		{
			ProviderType = ProviderType.Claude,
			ApiKey = "sk-ant-updated"
		});

		Assert.True(result.Success);

		var updatedSdkProvider = await dbContext.Providers.SingleAsync(item => item.Id == sdkProvider.Id);
		var unchangedCliProvider = await dbContext.Providers.SingleAsync(item => item.Id == cliProvider.Id);

		Assert.Equal("sk-ant-updated", updatedSdkProvider.ApiKey);
		Assert.Null(unchangedCliProvider.ApiKey);
	}

	[Fact]
	public async Task SaveAuthenticationAsync_DoesNotOverwriteCopilotByokProvider()
	{
		await using var dbContext = CreateDbContext();
		var byokProvider = new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Copilot BYOK",
			Type = ProviderType.Copilot,
			ConnectionMode = ProviderConnectionMode.SDK,
			ApiEndpoint = "https://api.openai.com/v1",
			ApiKey = "sk-provider",
			IsDefault = true,
			IsEnabled = true
		};

		dbContext.Providers.Add(byokProvider);
		await dbContext.SaveChangesAsync();

		var service = CreateService(dbContext);
		var result = await service.SaveAuthenticationAsync(new CommonProviderSetupRequest
		{
			ProviderType = ProviderType.Copilot,
			ApiKey = "github_pat_new"
		});

		Assert.True(result.Success);

		var providers = await dbContext.Providers
			.Where(item => item.Type == ProviderType.Copilot)
			.OrderBy(item => item.Name)
			.ToListAsync();

		Assert.Collection(providers,
			byok =>
			{
				Assert.Equal("Copilot BYOK", byok.Name);
				Assert.Equal("sk-provider", byok.ApiKey);
			},
			cli =>
			{
				Assert.Equal(ProviderConnectionMode.CLI, cli.ConnectionMode);
				Assert.Equal("github_pat_new", cli.ApiKey);
			});
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
