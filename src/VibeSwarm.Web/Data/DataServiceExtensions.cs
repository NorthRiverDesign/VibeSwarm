using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Shared.Data;

public static class DataServiceExtensions
{
    /// <summary>
    /// Supported database provider aliases mapped to canonical names.
    /// </summary>
    private static readonly Dictionary<string, string> ProviderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqlite"] = "sqlite",
        ["postgres"] = "postgresql",
        ["postgresql"] = "postgresql",
        ["sqlserver"] = "sqlserver",
        ["mssql"] = "sqlserver",
    };

    public static IServiceCollection AddVibeSwarmData(
        this IServiceCollection services,
        string connectionString,
        string databaseProvider = "sqlite")
    {
        var canonical = ResolveProviderName(databaseProvider);

        services.AddDbContext<VibeSwarmDbContext>(options =>
        {
            switch (canonical)
            {
                case "postgresql":
                    options.UseNpgsql(connectionString);
                    break;
                case "sqlserver":
                    options.UseSqlServer(connectionString);
                    break;
                default:
                    options.UseSqlite(connectionString);
                    break;
            }
        });

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
        services.AddScoped<AgentDetectionService>();

        return services;
    }

    /// <summary>
    /// Resolves a provider alias (e.g. "postgres", "mssql") to its canonical name.
    /// Throws if the provider is not recognized.
    /// </summary>
    public static string ResolveProviderName(string provider)
    {
        if (ProviderAliases.TryGetValue(provider, out var canonical))
            return canonical;

        throw new InvalidOperationException(
            $"Unsupported DATABASE_PROVIDER '{provider}'. " +
            $"Supported values: {string.Join(", ", ProviderAliases.Keys)}");
    }
}
