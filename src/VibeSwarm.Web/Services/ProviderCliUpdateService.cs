using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Keeps the provider CLIs on their latest release. Jobs run with self-updates switched off, so
/// without this a host that only runs jobs stays on whatever version was installed while the
/// CLIs, and VibeSwarm's wrappers, move on. A CLI is only updated while none of its provider's
/// jobs is running. Set <c>CliAutoUpdate:Enabled</c> to false to turn it off.
/// </summary>
public sealed class ProviderCliUpdateService : BackgroundService
{
	internal static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);
	private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IConfiguration _configuration;
	private readonly ILogger<ProviderCliUpdateService> _logger;
	private readonly Dictionary<string, DateTime> _lastUpdatedAt = new(StringComparer.Ordinal);

	public ProviderCliUpdateService(
		IServiceScopeFactory scopeFactory,
		IConfiguration configuration,
		ILogger<ProviderCliUpdateService> logger)
	{
		_scopeFactory = scopeFactory;
		_configuration = configuration;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!_configuration.GetValue("CliAutoUpdate:Enabled", true))
		{
			_logger.LogInformation("Provider CLI auto-update is turned off");
			return;
		}

		try
		{
			await Task.Delay(FirstCheckDelay, stoppingToken);
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await UpdateDueProvidersAsync(DateTime.UtcNow, stoppingToken);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					_logger.LogError(ex, "Error updating provider CLIs");
				}

				await Task.Delay(PollInterval, stoppingToken);
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
	}

	/// <summary>
	/// Updates each CLI that is due and idle. Providers that share an executable are updated once.
	/// </summary>
	internal async Task UpdateDueProvidersAsync(DateTime now, CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var providerService = scope.ServiceProvider.GetRequiredService<IProviderService>();
		var dbContext = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();

		var providers = (await providerService.GetAllAsync(cancellationToken))
			.Where(provider => provider.IsEnabled && provider.ConnectionMode == ProviderConnectionMode.CLI)
			.GroupBy(provider => $"{provider.Type}|{provider.ExecutablePath?.Trim()}")
			.ToList();
		if (providers.Count == 0)
		{
			return;
		}

		var activeStatuses = new[] { JobStatus.Started, JobStatus.Planning, JobStatus.Processing, JobStatus.Paused };
		var busyProviderIds = await dbContext.Jobs
			.Where(job => activeStatuses.Contains(job.Status))
			.Select(job => job.ProviderId)
			.Distinct()
			.ToListAsync(cancellationToken);

		foreach (var group in providers)
		{
			if (_lastUpdatedAt.TryGetValue(group.Key, out var last) && now - last < UpdateInterval)
			{
				continue;
			}

			if (group.Any(provider => busyProviderIds.Contains(provider.Id)))
			{
				_logger.LogDebug("Postponing the {ProviderType} CLI update while one of its jobs runs", group.First().Type);
				continue;
			}

			_lastUpdatedAt[group.Key] = now;
			var provider = group.First();
			var result = await providerService.UpdateCliAsync(provider.Id, cancellationToken);
			if (!result.Success)
			{
				_logger.LogWarning("Could not update the {ProviderType} CLI for {Provider}: {Error}", provider.Type, provider.Name, result.ErrorMessage);
			}
			else if (!string.Equals(result.PreviousVersion, result.NewVersion, StringComparison.Ordinal))
			{
				_logger.LogInformation("Updated the {ProviderType} CLI from {PreviousVersion} to {NewVersion}", provider.Type, result.PreviousVersion, result.NewVersion);
			}
		}
	}
}
