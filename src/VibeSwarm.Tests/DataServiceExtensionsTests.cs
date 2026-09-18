using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class DataServiceExtensionsTests
{
	[Theory]
	[InlineData("sqlite", "sqlite")]
	[InlineData("mysql", "mysql")]
	[InlineData("mariadb", "mysql")]
	[InlineData("MySQL", "mysql")]
	[InlineData("SQLite", "sqlite")]
	public void ResolveProviderName_NormalizesAliases(string provider, string expected)
	{
		var resolved = DataServiceExtensions.ResolveProviderName(provider);

		Assert.Equal(expected, resolved);
	}

	[Theory]
	[InlineData("postgres")]
	[InlineData("postgresql")]
	[InlineData("sqlserver")]
	[InlineData("mssql")]
	[InlineData("mongodb")]
	[InlineData("")]
	public void ResolveProviderName_ThrowsForUnsupportedProvider(string provider)
	{
		var ex = Assert.Throws<InvalidOperationException>(
			() => DataServiceExtensions.ResolveProviderName(provider));

		// The message echoes the rejected input, so only the advertised
		// "Supported values" list is checked for dropped providers.
		var supported = ex.Message[(ex.Message.IndexOf("Supported values:", StringComparison.Ordinal))..];
		Assert.Contains("sqlite", supported);
		Assert.Contains("mysql", supported);
		Assert.DoesNotContain("postgres", supported, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("sqlserver", supported, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("mssql", supported, StringComparison.OrdinalIgnoreCase);
	}
}
