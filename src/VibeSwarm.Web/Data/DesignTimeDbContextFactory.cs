using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VibeSwarm.Shared.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VibeSwarmDbContext>
{
	public VibeSwarmDbContext CreateDbContext(string[] args)
	{
		var runtimeDatabaseConfiguration = new DatabaseRuntimeConfigurationStore().Load();
		var provider = Environment.GetEnvironmentVariable("DATABASE_PROVIDER")
			?? runtimeDatabaseConfiguration?.Provider
			?? "sqlite";
		var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
			?? Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__DEFAULT")
			?? runtimeDatabaseConfiguration?.ConnectionString
			?? "Data Source=vibeswarm.db";

		var optionsBuilder = new DbContextOptionsBuilder<VibeSwarmDbContext>();
		DataServiceExtensions.ConfigureDbContext(optionsBuilder, connectionString, provider);

		return new VibeSwarmDbContext(optionsBuilder.Options);
	}
}
