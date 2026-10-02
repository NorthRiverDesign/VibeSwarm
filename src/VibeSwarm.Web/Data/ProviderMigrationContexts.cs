using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Provider-specific contexts that exist only to own migrations.
/// </summary>
/// <remarks>
/// EF Core bakes provider-specific SQL into a migration when it is generated: SQLite writes
/// <c>TEXT</c> and <c>INTEGER</c> columns, MySQL writes <c>char(36)</c>, <c>datetime(6)</c>
/// and so on. One migration set therefore cannot serve both providers — applying the SQLite
/// set to MySQL fails immediately with
/// "BLOB/TEXT column 'Id' used in key specification without a key length".
///
/// The documented fix is a migrations set per provider. Because EF resolves migrations by
/// context type, deriving a context per provider keeps both sets in this one project, each
/// with its own model snapshot, without splitting the solution into extra assemblies.
///
/// These types are never registered for dependency injection. The application resolves
/// <see cref="VibeSwarmDbContext"/> as it always has; these are constructed only to run
/// migrations, via <see cref="DataServiceExtensions.CreateMigrationContext"/>.
///
/// To add a migration, generate it for BOTH providers so the two sets stay in step:
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/VibeSwarm.Web \
///   --context SqliteVibeSwarmDbContext --output-dir Data/Migrations/Sqlite
/// dotnet ef migrations add &lt;Name&gt; --project src/VibeSwarm.Web \
///   --context MySqlVibeSwarmDbContext --output-dir Data/Migrations/MySql
/// </code>
/// <c>MigrationSetsStayInStepTests</c> fails the build if one set drifts from the other.
/// </remarks>
public class SqliteVibeSwarmDbContext : VibeSwarmDbContext
{
	public SqliteVibeSwarmDbContext(DbContextOptions<SqliteVibeSwarmDbContext> options)
		: base(options)
	{
	}
}

/// <inheritdoc cref="SqliteVibeSwarmDbContext"/>
public class MySqlVibeSwarmDbContext : VibeSwarmDbContext
{
	public MySqlVibeSwarmDbContext(DbContextOptions<MySqlVibeSwarmDbContext> options)
		: base(options)
	{
	}
}

/// <summary>
/// Design-time factory for the SQLite migration set.
/// </summary>
/// <remarks>
/// Uses a throwaway connection string: generating a migration only needs the provider's
/// type mappings, never a reachable database.
/// </remarks>
public class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqliteVibeSwarmDbContext>
{
	public SqliteVibeSwarmDbContext CreateDbContext(string[] args)
	{
		var options = new DbContextOptionsBuilder<SqliteVibeSwarmDbContext>()
			.UseSqlite(DesignTimeConnectionString.Resolve("Data Source=vibeswarm.design.db"))
			.Options;

		return new SqliteVibeSwarmDbContext(options);
	}
}

/// <summary>
/// Picks the connection string the EF tools should use at design time.
/// </summary>
internal static class DesignTimeConnectionString
{
	/// <summary>
	/// Returns the configured connection string when one is present, otherwise
	/// <paramref name="placeholder"/>.
	/// </summary>
	/// <remarks>
	/// Generating a migration needs no reachable database, so the placeholder is enough for
	/// <c>migrations add</c>. Applying one with <c>database update</c> does need a real
	/// target, so the environment and the saved runtime configuration are honoured — the
	/// same sources the application itself reads.
	/// </remarks>
	public static string Resolve(string placeholder)
	{
		var configured = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
			?? Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__DEFAULT")
			?? new DatabaseRuntimeConfigurationStore().Load()?.ConnectionString;

		return string.IsNullOrWhiteSpace(configured) ? placeholder : configured;
	}
}

/// <summary>
/// Design-time factory for the MySQL migration set.
/// </summary>
/// <remarks>
/// Pins the server version rather than calling <c>ServerVersion.AutoDetect</c> so migrations
/// can be generated without a running database — otherwise nobody could add a migration
/// without first standing up MySQL. 8.0 is the conservative target: the DDL it produces also
/// applies cleanly to MariaDB 10.5+, which is what this project is tested against.
/// </remarks>
public class MySqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<MySqlVibeSwarmDbContext>
{
	internal static readonly MySqlServerVersion DesignTimeServerVersion = new(new Version(8, 0, 21));

	public MySqlVibeSwarmDbContext CreateDbContext(string[] args)
	{
		var connectionString = DesignTimeConnectionString.Resolve(
			"Server=localhost;Database=vibeswarm_design;User=root;Password=");

		// The pinned version is used even against a real server so the generated DDL does not
		// change depending on whose database the tools happened to reach.
		var options = new DbContextOptionsBuilder<MySqlVibeSwarmDbContext>()
			.UseMySql(connectionString, DesignTimeServerVersion)
			.Options;

		return new MySqlVibeSwarmDbContext(options);
	}
}
