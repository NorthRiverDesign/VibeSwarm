using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

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
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IProviderUsageService, ProviderUsageService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddHttpClient("LocalInference");
        services.AddScoped<IInferenceProviderService, InferenceProviderService>();
        services.AddScoped<IInferenceService, OllamaInferenceService>();

        return services;
    }
}
