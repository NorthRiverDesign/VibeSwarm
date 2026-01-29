using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Worker;

public static class WorkerServiceExtensions
{
    public static IServiceCollection AddWorkerServices(this IServiceCollection services)
    {
        // Version control services
        services.AddVersionControlServices();

        // Core services for job coordination
        services.AddSingleton<IProviderHealthTracker, ProviderHealthTracker>();
        services.AddSingleton<JobQueueManager>();
        services.AddSingleton<IJobCoordinatorService, JobCoordinatorService>();
        services.AddSingleton<ProcessSupervisor>();

        // Background services
        services.AddHostedService<JobProcessingService>();
        services.AddHostedService<JobWatchdogService>();
        services.AddHostedService<JobCompletionMonitorService>();
        services.AddHostedService<IdeasProcessingService>();

        return services;
    }
}
