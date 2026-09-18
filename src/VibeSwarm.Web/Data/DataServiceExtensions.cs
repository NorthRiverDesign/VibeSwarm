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
		services.AddScoped<ISkillStorageService, SkillStorageService>();
		services.AddScoped<ISkillInstaller, ZipSkillInstaller>();
		services.AddScoped<ISkillInstaller, MarketplaceSkillInstaller>();
		services.AddScoped<ISkillInstaller, LocalPathSkillInstaller>();
		services.AddScoped<ISkillInstallerService, SkillInstallerService>();
		services.AddMemoryCache();
		services.AddScoped<IGitHubSkillCatalogClient, GitHubSkillCatalogClient>();
		services.AddHttpClient(GitHubSkillCatalogClient.HttpClientName, client =>
		{
			client.BaseAddress = new Uri("https://api.github.com/");
			client.DefaultRequestHeaders.UserAgent.ParseAdd("VibeSwarm-Skills-Installer");
			client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
			client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
			client.Timeout = TimeSpan.FromSeconds(30);
		});
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
			default:
				options.UseSqlite(connectionString);
				break;
		}

		// Surface the actual conflicting key values and the full row data when EF
		// throws tracking or SaveChanges exceptions. Without this, the error message
		// is generic and hides the offending Id.
		options.EnableSensitiveDataLogging();
		options.EnableDetailedErrors();
	}

	/// <summary>
	/// Creates a context that owns the migration set for the given provider.
	/// </summary>
	/// <remarks>
	/// Migrations are resolved by context type, and each provider has its own set (see
	/// <see cref="SqliteVibeSwarmDbContext"/>), so <see cref="VibeSwarmDbContext"/> itself
	/// owns none and calling <c>Database.MigrateAsync()</c> on it would silently do nothing.
	/// Anything that applies migrations must go through here.
	///
	/// The returned context is a <see cref="VibeSwarmDbContext"/>, so callers can also read
	/// and write through it normally. The caller owns disposal.
	/// </remarks>
	public static VibeSwarmDbContext CreateMigrationContext(
		string connectionString,
		string databaseProvider = "sqlite")
	{
		var canonical = ResolveProviderName(databaseProvider);

		if (canonical == "mysql")
		{
			var mySqlOptions = new DbContextOptionsBuilder<MySqlVibeSwarmDbContext>();
			ConfigureDbContext(mySqlOptions, connectionString, canonical);
			return new MySqlVibeSwarmDbContext(mySqlOptions.Options);
		}

		var sqliteOptions = new DbContextOptionsBuilder<SqliteVibeSwarmDbContext>();
		ConfigureDbContext(sqliteOptions, connectionString, canonical);
		return new SqliteVibeSwarmDbContext(sqliteOptions.Options);
	}

    /// <summary>
    /// Resolves a provider alias (e.g. "mariadb") to its canonical name.
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
