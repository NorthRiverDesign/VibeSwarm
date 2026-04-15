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
		["mysql"] = "mysql",
		["mariadb"] = "mysql",
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
		services.AddDbContext<VibeSwarmDbContext>(options =>
		{
			ConfigureDbContext(options, connectionString, databaseProvider);
		});

		services.AddScoped<IProviderService, ProviderService>();
		services.AddSingleton<IDatabaseRuntimeConfigurationStore>(_ => new DatabaseRuntimeConfigurationStore());
		services.AddSingleton<IProjectEnvironmentCredentialService, ProjectEnvironmentCredentialService>();
		services.AddScoped<IProjectService, ProjectService>();
		services.AddScoped<IJobService, JobService>();
		services.AddScoped<IJobScheduleService, JobScheduleService>();
		services.AddScoped<IJobTemplateService, JobTemplateService>();
		services.AddScoped<ISettingsService, SettingsService>();
		services.AddScoped<ICriticalErrorLogService, CriticalErrorLogService>();
		services.AddScoped<ISkillService, SkillService>();
		services.AddScoped<IAgentService, AgentService>();
		services.AddScoped<IMcpConfigService, McpConfigService>();
		services.AddScoped<IProjectMemoryService, ProjectMemoryService>();
		services.AddScoped<IIdeaService, IdeaService>();
		services.AddScoped<IUserService, UserService>();
		services.AddScoped<IProviderUsageService, ProviderUsageService>();
		services.AddScoped<IAutoPilotService, AutoPilotService>();
		services.AddScoped<AutoPilotService>();
		services.AddScoped<IDatabaseService, DatabaseService>();
		services.AddScoped<ICommonProviderSetupService, CommonProviderSetupService>();
		services.AddScoped<ProviderCliDetectionService>();
		services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddHttpClient("Inference");
        services.AddScoped<IInferenceProviderService, InferenceProviderService>();
        services.AddScoped<OllamaInferenceService>();
        services.AddScoped<GrokInferenceService>();
        services.AddScoped<IInferenceService, InferenceServiceDispatcher>();
        services.AddScoped<AgentDetectionService>();

		return services;
	}

	public static void ConfigureDbContext(
		DbContextOptionsBuilder options,
		string connectionString,
		string databaseProvider = "sqlite")
	{
		var canonical = ResolveProviderName(databaseProvider);

		switch (canonical)
		{
			case "mysql":
				options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
				break;
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
	}

    /// <summary>
    /// Resolves a provider alias (e.g. "postgres", "mssql") to its canonical name.
    /// Throws if the provider is not recognized.
    /// </summary>
	public static string ResolveProviderName(string provider)
	{
		if (string.IsNullOrWhiteSpace(provider))
		{
			throw new InvalidOperationException(
				$"Unsupported DATABASE_PROVIDER '{provider}'. " +
				$"Supported values: {string.Join(", ", ProviderAliases.Keys)}");
		}

		if (ProviderAliases.TryGetValue(provider, out var canonical))
			return canonical;

        throw new InvalidOperationException(
            $"Unsupported DATABASE_PROVIDER '{provider}'. " +
            $"Supported values: {string.Join(", ", ProviderAliases.Keys)}");
    }
}
