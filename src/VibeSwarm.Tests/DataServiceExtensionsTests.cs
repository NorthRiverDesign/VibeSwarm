using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class DataServiceExtensionsTests
{
	[Theory]
	[InlineData("sqlite", "sqlite")]
	[InlineData("mysql", "mysql")]
	[InlineData("mariadb", "mysql")]
	[InlineData("postgres", "postgresql")]
	[InlineData("postgresql", "postgresql")]
	[InlineData("mssql", "sqlserver")]
	[InlineData("sqlserver", "sqlserver")]
	public void ResolveProviderName_NormalizesAliases(string provider, string expected)
	{
		var resolved = DataServiceExtensions.ResolveProviderName(provider);

		Assert.Equal(expected, resolved);
	}
}
