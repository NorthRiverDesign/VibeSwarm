using Microsoft.Extensions.DependencyInjection;

namespace VibeSwarm.Worker;

public static class WorkerServiceExtensions
{
    public static IServiceCollection AddWorkerServices(this IServiceCollection services)
    {
        services.AddHostedService<JobProcessingService>();
        services.AddHostedService<JobWatchdogService>();
        return services;
    }
}
