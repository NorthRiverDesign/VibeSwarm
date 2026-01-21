using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared;

namespace VibeSwarm.Worker;

public class WorkerService : BackgroundService
{
    private readonly ILogger<WorkerService> _logger;

    public WorkerService(ILogger<WorkerService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker started at: {time}", DateTimeOffset.Now);

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            await Task.Delay(10000, stoppingToken);
        }

        _logger.LogInformation("Worker stopped at: {time}", DateTimeOffset.Now);
    }
}
