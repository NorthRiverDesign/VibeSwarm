using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Shared.Data;

public static class DataServiceExtensions
{
    public static IServiceCollection AddVibeSwarmData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<VibeSwarmDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<IProviderService, ProviderService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IJobService, JobService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<ISkillService, SkillService>();
        services.AddScoped<IMcpConfigService, McpConfigService>();
        services.AddScoped<IIdeaService, IdeaService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();

        return services;
    }
}
