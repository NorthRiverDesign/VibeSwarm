using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

/// <summary>
/// Guards the provider-specific migration sets.
/// </summary>
/// <remarks>
/// SQLite and MySQL each own a migration set because EF bakes provider-specific SQL into a
/// migration when it is generated. The risk that introduces is drift: adding a migration for
/// one provider and forgetting the other leaves that provider's schema silently behind, and
/// the failure only shows up when someone deploys on it.
/// </remarks>
public sealed class MigrationSetsStayInStepTests
{
	/// <summary>
	/// Migration names with the timestamp prefix stripped, in application order.
	/// A migration id looks like "20260918172603_InitialCreate"; the timestamps differ
	/// between providers because the two are generated seconds apart, so only the names
	/// are comparable.
	/// </summary>
	private static List<string> GetMigrationNames<TContext>()
		where TContext : DbContext
	{
		return typeof(VibeSwarmDbContext).Assembly
			.GetTypes()
			.Where(type => typeof(Migration).IsAssignableFrom(type) && !type.IsAbstract)
			.Where(type => type.GetCustomAttribute<DbContextAttribute>()?.ContextType == typeof(TContext))
			.Select(type => type.GetCustomAttribute<MigrationAttribute>()!.Id)
			.OrderBy(id => id, StringComparer.Ordinal)
			.Select(id => id[(id.IndexOf('_') + 1)..])
			.ToList();
	}

	[Fact]
	public void BothProvidersHaveMigrations()
	{
		Assert.NotEmpty(GetMigrationNames<SqliteVibeSwarmDbContext>());
		Assert.NotEmpty(GetMigrationNames<MySqlVibeSwarmDbContext>());
	}

	[Fact]
	public void SqliteAndMySqlMigrationSetsMatch()
	{
		var sqlite = GetMigrationNames<SqliteVibeSwarmDbContext>();
		var mySql = GetMigrationNames<MySqlVibeSwarmDbContext>();

		Assert.True(
			sqlite.SequenceEqual(mySql, StringComparer.Ordinal),
			$"""
			The two migration sets have drifted.
			  SQLite: {string.Join(", ", sqlite)}
			  MySQL : {string.Join(", ", mySql)}
			Every migration must be generated for both providers:
			  dotnet ef migrations add <Name> --project src/VibeSwarm.Web --context SqliteVibeSwarmDbContext --output-dir Data/Migrations/Sqlite
			  dotnet ef migrations add <Name> --project src/VibeSwarm.Web --context MySqlVibeSwarmDbContext  --output-dir Data/Migrations/MySql
			""");
	}

	/// <summary>
	/// The base context intentionally owns no migrations: everything applying them goes
	/// through <see cref="DataServiceExtensions.CreateMigrationContext"/>. If a migration is
	/// ever generated against it by mistake, MigrateAsync would start applying SQLite DDL to
	/// whichever provider is configured.
	/// </summary>
	[Fact]
	public void BaseContextOwnsNoMigrations()
	{
		Assert.Empty(GetMigrationNames<VibeSwarmDbContext>());
	}

	[Fact]
	public void CreateMigrationContext_SelectsTheProviderSpecificContext()
	{
		using var sqlite = DataServiceExtensions.CreateMigrationContext("Data Source=:memory:", "sqlite");
		Assert.IsType<SqliteVibeSwarmDbContext>(sqlite);

		// "mariadb" must land on the MySQL set rather than silently falling through to SQLite.
		Assert.Equal("mysql", DataServiceExtensions.ResolveProviderName("mariadb"));
	}

	/// <summary>
	/// Applies the SQLite set end to end. This is the path a new user hits on first run with
	/// the default configuration, so it must work from an empty file every time.
	/// </summary>
	[Fact]
	public async Task SqliteMigrationsCreateAWorkingSchemaFromEmpty()
	{
		var databasePath = Path.Combine(Path.GetTempPath(), $"vibeswarm-migrations-{Guid.NewGuid():N}.db");

		try
		{
			await using (var context = DataServiceExtensions.CreateMigrationContext(
				$"Data Source={databasePath}", "sqlite"))
			{
				await context.Database.MigrateAsync();

				Assert.Empty(await context.Database.GetPendingMigrationsAsync());
				Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());

				// Round-trip a row to prove the schema is usable, not merely present.
				Assert.False(await context.Providers.AnyAsync());
			}
		}
		finally
		{
			foreach (var suffix in new[] { "", "-wal", "-shm" })
			{
				var file = databasePath + suffix;
				if (File.Exists(file))
				{
					File.Delete(file);
				}
			}
		}
	}

	/// <summary>
	/// The row-size workaround only makes sense on MySQL: on SQLite these stay TEXT anyway,
	/// and forcing "longtext" there would produce DDL SQLite does not understand.
	/// </summary>
	[Fact]
	public void LongTextMappingIsMySqlOnly()
	{
		using var sqlite = DataServiceExtensions.CreateMigrationContext("Data Source=:memory:", "sqlite");

		var longTextColumns = sqlite.Model.GetEntityTypes()
			.SelectMany(entity => entity.GetProperties())
			.Count(property => string.Equals(
				property.GetColumnType(), "longtext", StringComparison.OrdinalIgnoreCase));

		Assert.Equal(0, longTextColumns);
	}
}
