using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VibeSwarm.Web.Services;

public class JobScheduleBackgroundService : BackgroundService
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<JobScheduleBackgroundService> _logger;

	public JobScheduleBackgroundService(
		IServiceScopeFactory scopeFactory,
		ILogger<JobScheduleBackgroundService> logger)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("Job schedule background service started");

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using var scope = _scopeFactory.CreateScope();
				var processor = scope.ServiceProvider.GetRequiredService<JobScheduleProcessor>();
				await processor.ProcessDueSchedulesAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing scheduled jobs");
			}

			try
			{
				await Task.Delay(PollInterval, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
		}

		_logger.LogInformation("Job schedule background service stopped");
	}
}
