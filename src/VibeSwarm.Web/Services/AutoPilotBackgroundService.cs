namespace VibeSwarm.Web.Services;

/// <summary>
/// Background service that polls active auto-pilot loops and processes them.
/// Each tick checks all running loops and advances them through the iteration cycle.
/// </summary>
public class AutoPilotBackgroundService : BackgroundService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<AutoPilotBackgroundService> _logger;
	private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

	public AutoPilotBackgroundService(
		IServiceScopeFactory scopeFactory,
		ILogger<AutoPilotBackgroundService> logger)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("Auto-Pilot Background Service started");

		// Brief startup delay to let other services initialize
		await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await ProcessActiveLoopsAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in Auto-Pilot background processing");
			}

			try
			{
				await Task.Delay(_pollInterval, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		_logger.LogInformation("Auto-Pilot Background Service stopped");
	}

	private async Task ProcessActiveLoopsAsync(CancellationToken cancellationToken)
	{
		using var scope = _scopeFactory.CreateScope();
		var service = scope.ServiceProvider.GetRequiredService<AutoPilotService>();

		var activeLoops = await service.GetActiveLoopsAsync(cancellationToken);

		foreach (var loop in activeLoops)
		{
			try
			{
				await service.ProcessTickAsync(loop.Id, cancellationToken);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing auto-pilot loop {LoopId} for project {ProjectId}",
					loop.Id, loop.ProjectId);
			}
		}
	}
}
